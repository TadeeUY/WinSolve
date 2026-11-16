//! Identical-file finder: same size, then a quick hash of the first and last 64 KB, then a full
//! SHA-256. Mirrors the managed `DuplicateFinder.cs` (which still picks the candidate files and
//! applies the safety exclusions); this does the heavy part: reading and hashing.

use rayon::prelude::*;
use rayon::ThreadPoolBuilder;
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};

pub const CHUNK: usize = 64 * 1024;

pub struct DupGroup {
    pub size: u64,
    pub hash: [u8; 32],
    /// Indexes into the input list, in input order.
    pub members: Vec<u32>,
}

pub trait DupReporter: Sync {
    fn cancelled(&self) -> bool;
    /// `done` of `total` same-size sets compared; `sets` duplicate sets found so far.
    fn progress(&self, done: u32, total: u32, sets: u32);
}

#[derive(Debug, PartialEq, Eq)]
pub struct Cancelled;

#[cfg(windows)]
fn open(path: &Path) -> std::io::Result<File> {
    use std::os::windows::fs::OpenOptionsExt;
    // Locked files (open in another program) can still be read.
    std::fs::OpenOptions::new()
        .read(true)
        .share_mode(0x1 | 0x2 | 0x4)
        .open(path)
}

#[cfg(not(windows))]
fn open(path: &Path) -> std::io::Result<File> {
    File::open(path)
}

enum HashError {
    Unreadable,
    Cancelled,
}

fn hash_file(path: &Path, quick_only: bool, rep: &dyn DupReporter) -> Result<[u8; 32], HashError> {
    let mut f = open(path).map_err(|_| HashError::Unreadable)?;
    let mut sha = Sha256::new();
    if !quick_only {
        // Chunked so closing the dialog stops hashing a 60 GB file right away.
        let mut buf = vec![0u8; 1 << 20];
        loop {
            if rep.cancelled() {
                return Err(HashError::Cancelled);
            }
            let n = f.read(&mut buf).map_err(|_| HashError::Unreadable)?;
            if n == 0 {
                break;
            }
            sha.update(&buf[..n]);
        }
        return Ok(sha.finalize().into());
    }

    let len = f.metadata().map_err(|_| HashError::Unreadable)?.len();
    let mut buf = vec![0u8; CHUNK];
    let n = read_full(&mut f, &mut buf).map_err(|_| HashError::Unreadable)?;
    sha.update(&buf[..n]);
    if len > (CHUNK as u64) * 2 {
        f.seek(SeekFrom::End(-(CHUNK as i64)))
            .map_err(|_| HashError::Unreadable)?;
        let n = read_full(&mut f, &mut buf).map_err(|_| HashError::Unreadable)?;
        sha.update(&buf[..n]);
    }
    Ok(sha.finalize().into())
}

fn read_full(f: &mut File, buf: &mut [u8]) -> std::io::Result<usize> {
    let mut got = 0;
    while got < buf.len() {
        let n = f.read(&mut buf[got..])?;
        if n == 0 {
            break;
        }
        got += n;
    }
    Ok(got)
}

/// Splits `members` by the hash of each file; files that can't be read are dropped.
fn group_by_hash(
    files: &[(PathBuf, u64)],
    members: &[u32],
    quick_only: impl Fn(u32) -> bool,
    rep: &dyn DupReporter,
) -> Result<Vec<([u8; 32], Vec<u32>)>, Cancelled> {
    let mut by_hash: HashMap<[u8; 32], Vec<u32>> = HashMap::new();
    for &m in members {
        match hash_file(&files[m as usize].0, quick_only(m), rep) {
            Ok(h) => by_hash.entry(h).or_default().push(m),
            Err(HashError::Cancelled) => return Err(Cancelled),
            Err(HashError::Unreadable) => {}
        }
    }
    let mut v: Vec<_> = by_hash.into_iter().filter(|(_, g)| g.len() > 1).collect();
    v.sort_by_key(|(_, g)| g[0]);
    Ok(v)
}

fn compare_set(
    files: &[(PathBuf, u64)],
    set: &[u32],
    size: u64,
    rep: &dyn DupReporter,
) -> Result<Vec<DupGroup>, Cancelled> {
    // Hard links are one file with several names: deleting one frees nothing.
    let mut seen: HashSet<(u64, u64)> = HashSet::new();
    let distinct: Vec<u32> = set
        .iter()
        .copied()
        .filter(|&i| match crate::fileid::file_id(&files[i as usize].0) {
            Some(id) => seen.insert(id),
            None => true,
        })
        .collect();
    if distinct.len() < 2 {
        return Ok(Vec::new());
    }
    let mut out = Vec::new();
    let small = size <= (CHUNK as u64) * 2; // the quick hash already covers the whole file
    for (quick, members) in group_by_hash(files, &distinct, |_| true, rep)? {
        if small {
            out.push(DupGroup {
                size,
                hash: quick,
                members,
            });
            continue;
        }
        for (full, members) in group_by_hash(files, &members, |_| false, rep)? {
            out.push(DupGroup {
                size,
                hash: full,
                members,
            });
        }
    }
    Ok(out)
}

pub fn find_duplicates(
    files: &[(PathBuf, u64)],
    workers: usize,
    rep: &dyn DupReporter,
) -> Result<Vec<DupGroup>, Cancelled> {
    let mut by_size: HashMap<u64, Vec<u32>> = HashMap::new();
    for (i, (_, size)) in files.iter().enumerate() {
        by_size.entry(*size).or_default().push(i as u32);
    }
    let mut sets: Vec<(u64, Vec<u32>)> = by_size.into_iter().filter(|(_, v)| v.len() > 1).collect();
    sets.sort_by(|a, b| b.0.cmp(&a.0)); // biggest first, deterministic
    let total = sets.len() as u32;
    rep.progress(0, total, 0);

    let done = AtomicU32::new(0);
    let found = AtomicU32::new(0);
    let stop = AtomicBool::new(false);
    let pool = ThreadPoolBuilder::new()
        .num_threads(workers.clamp(1, 16))
        .build()
        .expect("thread pool");
    let results: Vec<Result<Vec<DupGroup>, Cancelled>> = pool.install(|| {
        sets.par_iter()
            .map(|(size, set)| {
                if stop.load(Ordering::Relaxed) || rep.cancelled() {
                    stop.store(true, Ordering::Relaxed);
                    return Err(Cancelled);
                }
                let r = compare_set(files, set, *size, rep);
                match &r {
                    Ok(g) => {
                        found.fetch_add(g.len() as u32, Ordering::Relaxed);
                    }
                    Err(_) => stop.store(true, Ordering::Relaxed),
                }
                let d = done.fetch_add(1, Ordering::Relaxed) + 1;
                if d % 20 == 0 {
                    rep.progress(d, total, found.load(Ordering::Relaxed));
                }
                r
            })
            .collect()
    });

    let mut groups = Vec::new();
    for r in results {
        groups.extend(r?);
    }
    rep.progress(total, total, groups.len() as u32);
    Ok(groups)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::io::Write;

    struct Quiet(AtomicBool);
    impl DupReporter for Quiet {
        fn cancelled(&self) -> bool {
            self.0.load(Ordering::Relaxed)
        }
        fn progress(&self, _: u32, _: u32, _: u32) {}
    }

    fn tmp(tag: &str) -> PathBuf {
        let p = std::env::temp_dir().join(format!("winsolve-dupes-{tag}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&p);
        fs::create_dir_all(&p).unwrap();
        p
    }

    fn write(p: &Path, data: &[u8]) -> (PathBuf, u64) {
        fs::File::create(p).unwrap().write_all(data).unwrap();
        (p.to_path_buf(), data.len() as u64)
    }

    #[test]
    fn finds_identical_files_and_ignores_lookalikes() {
        let d = tmp("find");
        // Same size and same first/last 64 KB, different middle: only the full hash tells them apart.
        let mut a = vec![1u8; 300_000];
        let mut b = a.clone();
        b[150_000] = 9;
        let mut c = a.clone();
        c[150_000] = 9;
        a[0] = 1;
        let files = vec![
            write(&d.join("a1"), &a),
            write(&d.join("a2"), &a),
            write(&d.join("b1"), &b),
            write(&d.join("b2"), &c),
            write(&d.join("small1"), b"hello"),
            write(&d.join("small2"), b"hello"),
            write(&d.join("small3"), b"world"),
            write(&d.join("alone"), &[0u8; 12345]),
        ];
        let g = find_duplicates(&files, 4, &Quiet(AtomicBool::new(false))).unwrap();
        let mut sets: Vec<Vec<u32>> = g.iter().map(|g| g.members.clone()).collect();
        sets.sort();
        assert_eq!(sets, vec![vec![0, 1], vec![2, 3], vec![4, 5]]);
        let small = g.iter().find(|g| g.members == [4, 5]).unwrap();
        assert_eq!(small.size, 5);
        assert_eq!(small.hash, <[u8; 32]>::from(Sha256::digest(b"hello")));
        let big = g.iter().find(|g| g.members == [0, 1]).unwrap();
        assert_eq!(
            big.hash,
            <[u8; 32]>::from(Sha256::digest(&a)),
            "big sets report the full SHA-256"
        );
        fs::remove_dir_all(&d).unwrap();
    }

    #[test]
    fn same_size_different_content_is_not_a_duplicate() {
        let d = tmp("diff");
        let files = vec![write(&d.join("x"), b"aaaa"), write(&d.join("y"), b"bbbb")];
        assert!(find_duplicates(&files, 2, &Quiet(AtomicBool::new(false)))
            .unwrap()
            .is_empty());
        fs::remove_dir_all(&d).unwrap();
    }

    #[test]
    fn unreadable_files_are_skipped() {
        let d = tmp("gone");
        let mut files = vec![write(&d.join("x"), b"same"), write(&d.join("y"), b"same")];
        files.push((d.join("missing"), 4));
        let g = find_duplicates(&files, 2, &Quiet(AtomicBool::new(false))).unwrap();
        assert_eq!(g.len(), 1);
        assert_eq!(g[0].members, [0, 1]);
        fs::remove_dir_all(&d).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn hard_links_are_one_file() {
        let d = tmp("hard");
        let (p, n) = write(&d.join("orig"), b"content!");
        fs::hard_link(&p, d.join("link")).unwrap();
        let files = vec![(p, n), (d.join("link"), n)];
        assert!(find_duplicates(&files, 2, &Quiet(AtomicBool::new(false)))
            .unwrap()
            .is_empty());
        fs::remove_dir_all(&d).unwrap();
    }

    #[test]
    fn cancellation_returns_cancelled() {
        let d = tmp("cancel");
        let files = vec![write(&d.join("x"), b"same"), write(&d.join("y"), b"same")];
        assert!(find_duplicates(&files, 2, &Quiet(AtomicBool::new(true))).is_err());
        fs::remove_dir_all(&d).unwrap();
    }
}
