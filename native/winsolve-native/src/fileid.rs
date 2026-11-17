//! File identity (so hard links are one file) and link counts, per platform.

use std::path::Path;

/// Volume + file index: the same for every hard link of a file. `None` when it can't be read.
#[cfg(windows)]
pub fn file_id(path: &Path) -> Option<(u64, u64)> {
    use std::os::windows::fs::OpenOptionsExt;
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        GetFileInformationByHandle, BY_HANDLE_FILE_INFORMATION, FILE_SHARE_DELETE, FILE_SHARE_READ,
        FILE_SHARE_WRITE,
    };
    let f = std::fs::OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
        .open(path)
        .ok()?;
    let mut info: BY_HANDLE_FILE_INFORMATION = unsafe { std::mem::zeroed() };
    // SAFETY: the handle is valid for the lifetime of `f`, `info` is a plain out-struct.
    if unsafe { GetFileInformationByHandle(f.as_raw_handle() as _, &mut info) } == 0 {
        return None;
    }
    let index = ((info.nFileIndexHigh as u64) << 32) | info.nFileIndexLow as u64;
    Some((info.dwVolumeSerialNumber as u64, index))
}

#[cfg(unix)]
pub fn file_id(path: &Path) -> Option<(u64, u64)> {
    use std::os::unix::fs::MetadataExt;
    let m = std::fs::metadata(path).ok()?;
    Some((m.dev(), m.ino()))
}

#[cfg(not(any(windows, unix)))]
pub fn file_id(_path: &Path) -> Option<(u64, u64)> {
    None
}

/// How many names (hard links) the file has; 1 when it can't be read.
#[cfg(windows)]
pub fn link_count(path: &Path) -> u32 {
    use std::os::windows::ffi::OsStrExt;
    use std::os::windows::fs::OpenOptionsExt;
    use std::os::windows::io::AsRawHandle;
    use std::sync::OnceLock;
    use windows_sys::Win32::Foundation::GetLastError;
    use windows_sys::Win32::Storage::FileSystem::{
        GetFileInformationByHandle, BY_HANDLE_FILE_INFORMATION, FILE_FLAG_BACKUP_SEMANTICS,
        FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE,
    };
    use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};

    // Windows 11 24H2 / Server 2025 answer by name, without opening the file (several times
    // faster on WinSxS, where every file is checked).
    type ByName = unsafe extern "system" fn(*const u16, i32, *mut u8, u32) -> u8;
    static BY_NAME: OnceLock<Option<ByName>> = OnceLock::new();
    let by_name = *BY_NAME.get_or_init(|| unsafe {
        let k32: Vec<u16> = "kernel32.dll\0".encode_utf16().collect();
        let module = GetModuleHandleW(k32.as_ptr());
        if module.is_null() {
            return None;
        }
        GetProcAddress(module, b"GetFileInformationByName\0".as_ptr())
            .map(|p| std::mem::transmute::<_, ByName>(p))
    });
    if let Some(f) = by_name {
        let wide: Vec<u16> = path.as_os_str().encode_wide().chain(Some(0)).collect();
        // FILE_STAT_INFORMATION: 7 LARGE_INTEGERs, then FileAttributes, ReparseTag,
        // NumberOfLinks (offset 64), EffectiveAccess.
        let mut buf = [0u8; 72];
        // SAFETY: `wide` is NUL-terminated and `buf` is the 72 bytes the call is told about.
        if unsafe { f(wide.as_ptr(), 0, buf.as_mut_ptr(), 72) } != 0 {
            return u32::from_le_bytes(buf[64..68].try_into().unwrap());
        }
        let _ = unsafe { GetLastError() };
    }

    let Ok(file) = std::fs::OpenOptions::new()
        .access_mode(0)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(path)
    else {
        return 1;
    };
    let mut info: BY_HANDLE_FILE_INFORMATION = unsafe { std::mem::zeroed() };
    // SAFETY: valid handle, plain out-struct.
    if unsafe { GetFileInformationByHandle(file.as_raw_handle() as _, &mut info) } != 0 {
        info.nNumberOfLinks
    } else {
        1
    }
}

#[cfg(unix)]
pub fn link_count(path: &Path) -> u32 {
    use std::os::unix::fs::MetadataExt;
    std::fs::symlink_metadata(path)
        .map(|m| m.nlink() as u32)
        .unwrap_or(1)
}

#[cfg(not(any(windows, unix)))]
pub fn link_count(_path: &Path) -> u32 {
    1
}
