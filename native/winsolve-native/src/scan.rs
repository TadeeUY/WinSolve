//! Parallel disk scan, WizTree/WinDirStat style.
//!
//! Mirrors the managed scanner it replaces (`DiskScanner.cs`): reparse points are never followed
//! (except OneDrive placeholders), online-only cloud files take no space, files under 1 MB are
//! grouped per folder, other users' folders are skipped and hard links under WinSxS are counted
//! once.

use crate::paths::{extension_lower, is_other_users_data, is_same_or_inside, os_to_u16};
use rayon::ThreadPoolBuilder;
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::Mutex;

pub const SMALL_FILE_THRESHOLD: u64 = 1 << 20;

pub const FLAG_FILE: u8 = 1;
pub const FLAG_GROUP: u8 = 2;

// Windows file attribute bits.
const ATTR_DIRECTORY: u32 = 0x10;
const ATTR_REPARSE_POINT: u32 = 0x400;
// Cloud-file (OneDrive Files On-Demand) bits.
const RECALL_ON_OPEN: u32 = 0x40000;
const PINNED: u32 = 0x80000;
const UNPINNED: u32 = 0x100000;
const RECALL_ON_DATA_ACCESS: u32 = 0x400000;

pub struct ScanConfig {
    pub root: PathBuf,
    /// Other users' profile folders; never entered.
    pub excludes: Vec<String>,
    /// SID of the person at the keyboard (their `$Recycle.Bin` folder is allowed).
    pub own_sid: String,
    /// Folder whose hard-linked files are counted once (WinSxS).
    pub shared_links_root: Option<String>,
    pub workers: usize,
}

/// Progress and cancellation, implemented by the FFI layer (and by the tests).
pub trait Reporter: Sync {
    fn cancelled(&self) -> bool;
    fn progress(&self, files: u64, bytes: u64, dir: Option<&Path>);
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Node {
    /// Index of the parent in pre-order, `u32::MAX` for the root.
    pub parent: u32,
    pub flags: u8,
    /// Total size including everything below it.
    pub size: u64,
    pub files: u64,
    /// UTF-16 name (empty for the root and for groups).
    pub name: Vec<u16>,
}

#[derive(Debug)]
pub struct ScanResult {
    /// Pre-order, children sorted by size (largest first). `nodes[0]` is the root.
    pub nodes: Vec<Node>,
    /// (extension, size, count), largest first.
    pub extensions: Vec<(String, u64, u64)>,
    pub inaccessible: u64,
}

#[derive(Debug, PartialEq, Eq)]
pub struct Cancelled;

struct BigFile {
    name: Vec<u16>,
    size: u64,
}

struct DirOut {
    parent: usize,
    name: Vec<u16>,
    big: Vec<BigFile>,
    small_size: u64,
    small_count: u64,
    /// Sum of this folder's own files (big + small).
    own_size: u64,
    own_count: u64,
}

struct Ctx<'a> {
    cfg: &'a ScanConfig,
    reporter: &'a dyn Reporter,
    next_id: AtomicUsize,
    files: AtomicU64,
    bytes: AtomicU64,
    inaccessible: AtomicU64,
    cancelled: std::sync::atomic::AtomicBool,
    out: Mutex<Vec<(usize, DirOut)>>,
    ext: Mutex<HashMap<String, (u64, u64)>>,
}

struct Entry {
    name: Vec<u16>,
    is_dir: bool,
    full: PathBuf,
}

#[cfg(windows)]
fn classify(e: &fs::DirEntry) -> Option<(bool, u64)> {
    use std::os::windows::fs::MetadataExt;
    let m = e.metadata().ok()?; // no extra system call: comes with the directory listing
    let attrs = m.file_attributes();
    let reparse = attrs & ATTR_REPARSE_POINT != 0;
    let cloud = attrs & (RECALL_ON_OPEN | RECALL_ON_DATA_ACCESS | PINNED | UNPINNED) != 0;
    if reparse && !cloud {
        return None; // junction, symlink, mount point
    }
    let is_dir = attrs & ATTR_DIRECTORY != 0;
    // Online-only cloud files take no space on this disk.
    let len = if !is_dir && attrs & RECALL_ON_DATA_ACCESS != 0 {
        0
    } else {
        m.len()
    };
    Some((is_dir, len))
}

#[cfg(not(windows))]
fn classify(e: &fs::DirEntry) -> Option<(bool, u64)> {
    let t = e.file_type().ok()?;
    if t.is_symlink() {
        return None;
    }
    if t.is_dir() {
        return Some((true, 0));
    }
    Some((false, e.metadata().ok()?.len()))
}

// Silence "unused" for the Windows-only constants when building elsewhere.
#[cfg(not(windows))]
const _: [u32; 6] = [
    ATTR_DIRECTORY,
    ATTR_REPARSE_POINT,
    RECALL_ON_OPEN,
    PINNED,
    UNPINNED,
    RECALL_ON_DATA_ACCESS,
];

fn scan_dir<'s>(
    scope: &rayon::Scope<'s>,
    ctx: &'s Ctx<'s>,
    id: usize,
    parent: usize,
    name: Vec<u16>,
    path: PathBuf,
    depth: u32,
) {
    if ctx.cancelled.load(Ordering::Relaxed) || ctx.reporter.cancelled() {
        ctx.cancelled.store(true, Ordering::Relaxed);
        return;
    }
    let cfg = ctx.cfg;
    // Most of WinSxS is the same files as System32 & co. (hard links): count them once, where
    // Windows uses them, like "Analyze Component Store" does.
    let shared_links = cfg
        .shared_links_root
        .as_deref()
        .is_some_and(|r| is_same_or_inside(&path.to_string_lossy(), r));

    let mut out = DirOut {
        parent,
        name,
        big: Vec::new(),
        small_size: 0,
        small_count: 0,
        own_size: 0,
        own_count: 0,
    };
    let mut subdirs: Vec<(usize, Vec<u16>, PathBuf)> = Vec::new();
    let mut ext_local: HashMap<String, (u64, u64)> = HashMap::new();
    let mut files_here = 0u64;
    let mut bytes_here = 0u64;

    match fs::read_dir(&path) {
        Err(_) => {
            ctx.inaccessible.fetch_add(1, Ordering::Relaxed);
        }
        Ok(rd) => {
            for (n, item) in rd.enumerate() {
                if n % 4096 == 4095 && ctx.reporter.cancelled() {
                    ctx.cancelled.store(true, Ordering::Relaxed);
                    break;
                }
                let Ok(de) = item else { continue };
                let Some((is_dir, mut len)) = classify(&de) else {
                    continue;
                };
                let entry = Entry {
                    name: os_to_u16(&de.file_name()),
                    is_dir,
                    full: de.path(),
                };

                if entry.is_dir {
                    // Other Windows users' folders stay private.
                    if is_other_users_data(
                        &entry.full.to_string_lossy(),
                        &cfg.excludes,
                        &cfg.own_sid,
                    ) {
                        continue;
                    }
                    let child = ctx.next_id.fetch_add(1, Ordering::Relaxed);
                    subdirs.push((child, entry.name, entry.full));
                    continue;
                }

                if shared_links && len > 0 && crate::fileid::link_count(&entry.full) > 1 {
                    len = 0;
                }
                out.own_size += len;
                out.own_count += 1;
                files_here += 1;
                bytes_here += len;

                let ext = extension_lower(&entry.name);
                let key = if ext.is_empty() {
                    "(no extension)".to_string()
                } else {
                    ext
                };
                let slot = ext_local.entry(key).or_insert((0, 0));
                slot.0 += len;
                slot.1 += 1;

                if len >= SMALL_FILE_THRESHOLD {
                    out.big.push(BigFile {
                        name: entry.name,
                        size: len,
                    });
                } else {
                    out.small_size += len;
                    out.small_count += 1;
                }
            }
        }
    }

    let files = ctx.files.fetch_add(files_here, Ordering::Relaxed) + files_here;
    let bytes = ctx.bytes.fetch_add(bytes_here, Ordering::Relaxed) + bytes_here;
    ctx.reporter
        .progress(files, bytes, if depth <= 2 { Some(&path) } else { None });

    if !ext_local.is_empty() {
        let mut g = ctx.ext.lock().unwrap();
        for (k, (s, c)) in ext_local {
            let slot = g.entry(k).or_insert((0, 0));
            slot.0 += s;
            slot.1 += c;
        }
    }
    ctx.out.lock().unwrap().push((id, out));

    // Subfolders go to the pool: every level is read in parallel, not only the first ones.
    for (child_id, child_name, child_path) in subdirs {
        scope.spawn(move |s| scan_dir(s, ctx, child_id, id, child_name, child_path, depth + 1));
    }
}

pub fn scan(cfg: &ScanConfig, reporter: &dyn Reporter) -> Result<ScanResult, Cancelled> {
    let ctx = Ctx {
        cfg,
        reporter,
        next_id: AtomicUsize::new(1),
        files: AtomicU64::new(0),
        bytes: AtomicU64::new(0),
        inaccessible: AtomicU64::new(0),
        cancelled: std::sync::atomic::AtomicBool::new(false),
        out: Mutex::new(Vec::new()),
        ext: Mutex::new(HashMap::new()),
    };
    // Several folders are read at once: a directory read mostly waits for the disk.
    let pool = ThreadPoolBuilder::new()
        .num_threads(cfg.workers.clamp(1, 64))
        .build()
        .expect("thread pool");
    pool.scope(|s| scan_dir(s, &ctx, 0, usize::MAX, Vec::new(), cfg.root.clone(), 0));
    if ctx.cancelled.load(Ordering::Relaxed) || reporter.cancelled() {
        return Err(Cancelled);
    }

    let total = ctx.next_id.load(Ordering::Relaxed);
    let mut dirs: Vec<Option<DirOut>> = (0..total).map(|_| None).collect();
    for (id, d) in ctx.out.into_inner().unwrap() {
        dirs[id] = Some(d);
    }

    // Totals bottom-up: a folder's id is always larger than its parent's.
    let mut total_size = vec![0u64; total];
    let mut total_files = vec![0u64; total];
    for id in (0..total).rev() {
        let Some(d) = &dirs[id] else { continue };
        total_size[id] += d.own_size;
        total_files[id] += d.own_count;
        if d.parent != usize::MAX {
            let (s, f) = (total_size[id], total_files[id]);
            total_size[d.parent] += s;
            total_files[d.parent] += f;
        }
    }
    let mut children: Vec<Vec<usize>> = vec![Vec::new(); total];
    for (id, d) in dirs.iter().enumerate() {
        if let Some(d) = d {
            if d.parent != usize::MAX {
                children[d.parent].push(id);
            }
        }
    }

    enum Child {
        Dir(usize),
        Big(usize, usize), // (dir id, index in its big list)
        Group(usize),
    }
    let mut nodes: Vec<Node> = Vec::with_capacity(total + total / 2);
    // (child, parent index in `nodes`)
    let mut stack: Vec<(Child, u32)> = Vec::new();
    nodes.push(Node {
        parent: u32::MAX,
        flags: 0,
        size: total_size[0],
        files: total_files[0],
        name: Vec::new(),
    });
    let push_children = |id: usize, parent_idx: u32, stack: &mut Vec<(Child, u32)>| {
        let d = dirs[id].as_ref().unwrap();
        let mut kids: Vec<(u64, Child)> = Vec::with_capacity(children[id].len() + d.big.len() + 1);
        for &c in &children[id] {
            kids.push((total_size[c], Child::Dir(c)));
        }
        for (i, b) in d.big.iter().enumerate() {
            kids.push((b.size, Child::Big(id, i)));
        }
        if d.small_count > 0 {
            kids.push((d.small_size, Child::Group(id)));
        }
        // Largest first; pushed in reverse so the largest is emitted first.
        kids.sort_by(|a, b| b.0.cmp(&a.0));
        for (_, c) in kids.into_iter().rev() {
            stack.push((c, parent_idx));
        }
    };
    push_children(0, 0, &mut stack);
    while let Some((child, parent_idx)) = stack.pop() {
        let idx = nodes.len() as u32;
        match child {
            Child::Dir(c) => {
                let d = dirs[c].as_ref().unwrap();
                nodes.push(Node {
                    parent: parent_idx,
                    flags: 0,
                    size: total_size[c],
                    files: total_files[c],
                    name: d.name.clone(),
                });
                push_children(c, idx, &mut stack);
            }
            Child::Big(dir, i) => {
                let b = &dirs[dir].as_ref().unwrap().big[i];
                nodes.push(Node {
                    parent: parent_idx,
                    flags: FLAG_FILE,
                    size: b.size,
                    files: 1,
                    name: b.name.clone(),
                });
            }
            Child::Group(dir) => {
                let d = dirs[dir].as_ref().unwrap();
                nodes.push(Node {
                    parent: parent_idx,
                    flags: FLAG_FILE | FLAG_GROUP,
                    size: d.small_size,
                    files: d.small_count,
                    name: Vec::new(),
                });
            }
        }
    }

    let mut extensions: Vec<(String, u64, u64)> = ctx
        .ext
        .into_inner()
        .unwrap()
        .into_iter()
        .map(|(k, (s, c))| (k, s, c))
        .collect();
    extensions.sort_by(|a, b| b.1.cmp(&a.1).then_with(|| a.0.cmp(&b.0)));

    Ok(ScanResult {
        nodes,
        extensions,
        inaccessible: ctx.inaccessible.load(Ordering::Relaxed),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use std::sync::atomic::AtomicBool;

    struct Quiet(AtomicBool);
    impl Reporter for Quiet {
        fn cancelled(&self) -> bool {
            self.0.load(Ordering::Relaxed)
        }
        fn progress(&self, _: u64, _: u64, _: Option<&Path>) {}
    }

    fn cfg(root: &Path) -> ScanConfig {
        ScanConfig {
            root: root.to_path_buf(),
            excludes: vec![],
            own_sid: "S-1-5-21-1".into(),
            shared_links_root: None,
            workers: 4,
        }
    }

    fn write(path: &Path, bytes: usize) {
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::File::create(path)
            .unwrap()
            .write_all(&vec![7u8; bytes])
            .unwrap();
    }

    fn name(n: &Node) -> String {
        String::from_utf16_lossy(&n.name)
    }

    fn tmp(tag: &str) -> PathBuf {
        let p = std::env::temp_dir().join(format!("winsolve-native-{tag}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&p);
        fs::create_dir_all(&p).unwrap();
        p
    }

    #[test]
    fn totals_grouping_and_order() {
        let root = tmp("scan");
        write(&root.join("big.bin"), 3 << 20); // >= 1 MB: its own node
        write(&root.join("a/one.txt"), 100);
        write(&root.join("a/two.TXT"), 200);
        write(&root.join("a/deep/huge.iso"), 5 << 20);
        write(&root.join("b/small.log"), 10);

        let r = scan(&cfg(&root), &Quiet(AtomicBool::new(false))).unwrap();
        let total = (3 << 20) + 100 + 200 + (5 << 20) + 10;
        assert_eq!(r.nodes[0].size, total);
        assert_eq!(r.nodes[0].files, 5);
        assert_eq!(r.nodes[0].parent, u32::MAX);

        // Children of the root, largest first: a (5 MB+300) > big.bin (3 MB) > b (10).
        let kids: Vec<&Node> = r.nodes.iter().filter(|n| n.parent == 0).collect();
        assert_eq!(
            kids.iter().map(|n| name(n)).collect::<Vec<_>>(),
            ["a", "big.bin", "b"]
        );
        assert!(kids.windows(2).all(|w| w[0].size >= w[1].size));

        // "a" holds a group for its two small files plus the deep folder.
        let a_idx = r
            .nodes
            .iter()
            .position(|n| n.parent == 0 && name(n) == "a")
            .unwrap() as u32;
        let group = r
            .nodes
            .iter()
            .find(|n| n.parent == a_idx && n.flags & FLAG_GROUP != 0)
            .unwrap();
        assert_eq!((group.size, group.files), (300, 2));

        // Parents always come before their children.
        for (i, n) in r.nodes.iter().enumerate().skip(1) {
            assert!((n.parent as usize) < i);
        }

        // .txt/.TXT are one extension; files without one are counted too.
        let txt = r.extensions.iter().find(|e| e.0 == ".txt").unwrap();
        assert_eq!((txt.1, txt.2), (300, 2));
        assert_eq!(r.extensions[0].0, ".iso");
        assert_eq!(r.inaccessible, 0);
        fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn skips_other_users_folders() {
        let root = tmp("excl");
        write(&root.join("mine/x.txt"), 10);
        write(&root.join("bob/secret.txt"), 999);
        let mut c = cfg(&root);
        c.excludes = vec![root.join("bob").to_string_lossy().into_owned()];
        let r = scan(&c, &Quiet(AtomicBool::new(false))).unwrap();
        assert_eq!(r.nodes[0].size, 10);
        assert!(r.nodes.iter().all(|n| name(n) != "bob"));
        fs::remove_dir_all(&root).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn never_follows_symlinks_and_counts_hard_links_once_in_shared_folder() {
        let root = tmp("links");
        write(&root.join("real/f.bin"), 2 << 20);
        std::os::unix::fs::symlink(root.join("real"), root.join("link")).unwrap();
        let r = scan(&cfg(&root), &Quiet(AtomicBool::new(false))).unwrap();
        assert_eq!(
            r.nodes[0].size,
            2 << 20,
            "the symlinked folder must not be counted twice"
        );

        let shared = root.join("shared");
        write(&shared.join("a.bin"), 2 << 20);
        fs::hard_link(shared.join("a.bin"), shared.join("b.bin")).unwrap();
        let mut c = cfg(&shared);
        c.shared_links_root = Some(shared.to_string_lossy().into_owned());
        let r = scan(&c, &Quiet(AtomicBool::new(false))).unwrap();
        assert_eq!(
            r.nodes[0].size, 0,
            "files with several names count as zero under the shared folder"
        );
        c.shared_links_root = None;
        let r = scan(&c, &Quiet(AtomicBool::new(false))).unwrap();
        assert_eq!(r.nodes[0].size, 4 << 20);
        fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn unreadable_root_is_counted_not_fatal() {
        let r = scan(
            &cfg(Path::new("/definitely/not/here")),
            &Quiet(AtomicBool::new(false)),
        )
        .unwrap();
        assert_eq!(r.nodes.len(), 1);
        assert_eq!(r.inaccessible, 1);
    }

    #[test]
    fn can_be_cancelled() {
        let root = tmp("cancel");
        write(&root.join("a/x"), 1);
        let r = scan(&cfg(&root), &Quiet(AtomicBool::new(true)));
        assert_eq!(r.unwrap_err(), Cancelled);
        fs::remove_dir_all(&root).unwrap();
    }
}
