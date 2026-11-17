//! WinSolve native core: the heavy file-system work (disk scan, duplicate hashing), exposed
//! through a small C ABI that the C# app loads from an absolute path it has verified.
//!
//! Everything here is read-only: nothing is deleted, moved or written.
//!
//! Conventions: strings are UTF-16 (pointer + length in code units, no NUL needed); results come
//! back as a byte buffer (see `format`) that the caller releases with `ws_free`; the return value
//! is 0 on success, 1 when cancelled, 2 for a bad argument and 3 for an internal error (a panic
//! never crosses the boundary). `cancel` points to an int the caller sets to non-zero to stop.

pub mod dupes;
pub mod fileid;
pub mod format;
pub mod paths;
pub mod scan;

use std::ffi::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::Path;
use std::sync::atomic::{AtomicI32, Ordering};

/// Bump when the exports or the binary formats change; the C# side refuses a different value.
pub const ABI_VERSION: u32 = 1;

pub const OK: i32 = 0;
pub const CANCELLED: i32 = 1;
pub const BAD_ARGUMENT: i32 = 2;
pub const INTERNAL_ERROR: i32 = 3;

#[repr(C)]
pub struct WsBuffer {
    pub ptr: *mut u8,
    pub len: u64,
}

pub type ScanProgressFn = Option<
    unsafe extern "C" fn(user: *mut c_void, files: u64, bytes: u64, dir: *const u16, dir_len: u32),
>;
pub type DupProgressFn =
    Option<unsafe extern "C" fn(user: *mut c_void, done: u32, total: u32, sets: u32)>;

struct User(*mut c_void);
// SAFETY: the pointer is only handed back, untouched, to the caller's own callback.
unsafe impl Send for User {}
unsafe impl Sync for User {}

struct Cancel(*const i32);
// SAFETY: read-only access to an int the caller owns for the duration of the call.
unsafe impl Send for Cancel {}
unsafe impl Sync for Cancel {}
impl Cancel {
    fn is_set(&self) -> bool {
        !self.0.is_null()
            && unsafe { AtomicI32::from_ptr(self.0 as *mut i32).load(Ordering::Relaxed) != 0 }
    }
}

unsafe fn slice<'a>(ptr: *const u16, len: u32) -> &'a [u16] {
    if ptr.is_null() || len == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(ptr, len as usize)
    }
}

unsafe fn give(out: *mut WsBuffer, bytes: Vec<u8>) {
    let boxed = bytes.into_boxed_slice();
    let len = boxed.len() as u64;
    (*out).ptr = Box::into_raw(boxed) as *mut u8;
    (*out).len = len;
}

#[no_mangle]
pub extern "C" fn ws_abi_version() -> u32 {
    ABI_VERSION
}

/// Releases a buffer returned by `ws_scan` / `ws_find_duplicates`.
///
/// # Safety
/// `ptr`/`len` must be exactly what a previous call returned, and be freed once.
#[no_mangle]
pub unsafe extern "C" fn ws_free(ptr: *mut u8, len: u64) {
    if !ptr.is_null() {
        drop(Box::from_raw(std::slice::from_raw_parts_mut(
            ptr,
            len as usize,
        )));
    }
}

struct ScanHooks {
    cancel: Cancel,
    progress: ScanProgressFn,
    user: User,
}

impl scan::Reporter for ScanHooks {
    fn cancelled(&self) -> bool {
        self.cancel.is_set()
    }
    fn progress(&self, files: u64, bytes: u64, dir: Option<&Path>) {
        let Some(f) = self.progress else { return };
        match dir {
            Some(d) => {
                let units = paths::os_to_u16(d.as_os_str());
                unsafe {
                    f(
                        self.user.0,
                        files,
                        bytes,
                        units.as_ptr(),
                        units.len() as u32,
                    )
                }
            }
            None => unsafe { f(self.user.0, files, bytes, std::ptr::null(), 0) },
        }
    }
}

/// Scans `root`. `excludes` is a newline-separated list of other users' profile folders.
///
/// # Safety
/// All pointers must be valid for the given lengths for the duration of the call; `out` must point
/// to a `WsBuffer`; `cancel` may be null.
#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn ws_scan(
    root: *const u16,
    root_len: u32,
    excludes: *const u16,
    excludes_len: u32,
    own_sid: *const u16,
    own_sid_len: u32,
    shared_links_root: *const u16,
    shared_links_root_len: u32,
    workers: u32,
    cancel: *const i32,
    progress: ScanProgressFn,
    user: *mut c_void,
    out: *mut WsBuffer,
) -> i32 {
    if root.is_null() || root_len == 0 || out.is_null() {
        return BAD_ARGUMENT;
    }
    (*out).ptr = std::ptr::null_mut();
    (*out).len = 0;
    let result = catch_unwind(AssertUnwindSafe(|| {
        let text = |p, n| String::from_utf16_lossy(slice(p, n));
        let shared = text(shared_links_root, shared_links_root_len);
        let cfg = scan::ScanConfig {
            root: paths::u16_to_path(slice(root, root_len)),
            excludes: text(excludes, excludes_len)
                .split('\n')
                .filter(|s| !s.is_empty())
                .map(str::to_owned)
                .collect(),
            own_sid: text(own_sid, own_sid_len),
            shared_links_root: (!shared.is_empty()).then_some(shared),
            workers: workers as usize,
        };
        let hooks = ScanHooks {
            cancel: Cancel(cancel),
            progress,
            user: User(user),
        };
        match scan::scan(&cfg, &hooks) {
            Ok(r) => {
                give(out, format::encode_scan(&r));
                OK
            }
            Err(scan::Cancelled) => CANCELLED,
        }
    }));
    result.unwrap_or(INTERNAL_ERROR)
}

struct DupHooks {
    cancel: Cancel,
    progress: DupProgressFn,
    user: User,
}

impl dupes::DupReporter for DupHooks {
    fn cancelled(&self) -> bool {
        self.cancel.is_set()
    }
    fn progress(&self, done: u32, total: u32, sets: u32) {
        if let Some(f) = self.progress {
            unsafe { f(self.user.0, done, total, sets) }
        }
    }
}

/// Finds identical files among `count` candidates. `paths` holds the NUL-terminated UTF-16 paths
/// back to back (`paths_len` code units in all) and `sizes` their sizes. The result refers to the
/// candidates by index.
///
/// # Safety
/// All pointers must be valid for the given lengths for the duration of the call.
#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn ws_find_duplicates(
    paths_buf: *const u16,
    paths_len: u64,
    sizes: *const u64,
    count: u32,
    workers: u32,
    cancel: *const i32,
    progress: DupProgressFn,
    user: *mut c_void,
    out: *mut WsBuffer,
) -> i32 {
    if out.is_null() || (count > 0 && (paths_buf.is_null() || sizes.is_null())) {
        return BAD_ARGUMENT;
    }
    (*out).ptr = std::ptr::null_mut();
    (*out).len = 0;
    let result = catch_unwind(AssertUnwindSafe(|| {
        let all = if count == 0 {
            &[][..]
        } else {
            std::slice::from_raw_parts(paths_buf, paths_len as usize)
        };
        let sizes = if count == 0 {
            &[][..]
        } else {
            std::slice::from_raw_parts(sizes, count as usize)
        };
        let mut files = Vec::with_capacity(count as usize);
        let mut parts = all.split(|&c| c == 0);
        for size in sizes {
            match parts.next() {
                Some(p) if !p.is_empty() => files.push((paths::u16_to_path(p), *size)),
                _ => return BAD_ARGUMENT,
            }
        }
        let hooks = DupHooks {
            cancel: Cancel(cancel),
            progress,
            user: User(user),
        };
        match dupes::find_duplicates(&files, workers as usize, &hooks) {
            Ok(g) => {
                give(out, format::encode_dupes(&g));
                OK
            }
            Err(dupes::Cancelled) => CANCELLED,
        }
    }));
    result.unwrap_or(INTERNAL_ERROR)
}

#[cfg(test)]
mod ffi_tests {
    use super::*;
    use std::fs;
    use std::io::Write;

    fn u16s(s: &str) -> Vec<u16> {
        s.encode_utf16().collect()
    }

    #[test]
    fn scan_and_duplicates_through_the_c_abi() {
        let dir = std::env::temp_dir().join(format!("winsolve-ffi-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        for n in ["a.txt", "b.txt"] {
            fs::File::create(dir.join(n))
                .unwrap()
                .write_all(b"same content")
                .unwrap();
        }

        let root = u16s(&dir.to_string_lossy());
        let sid = u16s("S-1-5-21-1");
        let mut out = WsBuffer {
            ptr: std::ptr::null_mut(),
            len: 0,
        };
        let rc = unsafe {
            ws_scan(
                root.as_ptr(),
                root.len() as u32,
                std::ptr::null(),
                0,
                sid.as_ptr(),
                sid.len() as u32,
                std::ptr::null(),
                0,
                2,
                std::ptr::null(),
                None,
                std::ptr::null_mut(),
                &mut out,
            )
        };
        assert_eq!(rc, OK);
        let bytes = unsafe { std::slice::from_raw_parts(out.ptr, out.len as usize) }.to_vec();
        assert_eq!(&bytes[0..4], b"WSN1");
        assert_eq!(u32::from_le_bytes(bytes[4..8].try_into().unwrap()), 2); // root + one group of small files
        unsafe { ws_free(out.ptr, out.len) };

        let mut all: Vec<u16> = Vec::new();
        for n in ["a.txt", "b.txt"] {
            all.extend(u16s(&dir.join(n).to_string_lossy()));
            all.push(0);
        }
        let sizes = [12u64, 12];
        let rc = unsafe {
            ws_find_duplicates(
                all.as_ptr(),
                all.len() as u64,
                sizes.as_ptr(),
                2,
                2,
                std::ptr::null(),
                None,
                std::ptr::null_mut(),
                &mut out,
            )
        };
        assert_eq!(rc, OK);
        let bytes = unsafe { std::slice::from_raw_parts(out.ptr, out.len as usize) }.to_vec();
        assert_eq!(&bytes[0..4], b"WSD1");
        assert_eq!(u32::from_le_bytes(bytes[4..8].try_into().unwrap()), 1);
        unsafe { ws_free(out.ptr, out.len) };

        // A cancelled call reports it and returns no buffer.
        let flag = 1i32;
        let rc = unsafe {
            ws_find_duplicates(
                all.as_ptr(),
                all.len() as u64,
                sizes.as_ptr(),
                2,
                2,
                &flag,
                None,
                std::ptr::null_mut(),
                &mut out,
            )
        };
        assert_eq!(rc, CANCELLED);
        assert!(out.ptr.is_null());

        // Bad arguments are refused, not dereferenced.
        let rc = unsafe {
            ws_scan(
                std::ptr::null(),
                0,
                std::ptr::null(),
                0,
                std::ptr::null(),
                0,
                std::ptr::null(),
                0,
                1,
                std::ptr::null(),
                None,
                std::ptr::null_mut(),
                &mut out,
            )
        };
        assert_eq!(rc, BAD_ARGUMENT);
        fs::remove_dir_all(&dir).unwrap();
    }
}
