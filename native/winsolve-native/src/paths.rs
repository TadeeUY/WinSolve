//! Path helpers shared by the scanner. Comparisons are case-insensitive and treat `/` and `\`
//! alike, so the same code is exercised by the tests on any platform.

use std::ffi::OsStr;
use std::path::PathBuf;

/// UTF-16 code units of an OS string (exact on Windows, even for unpaired surrogates).
#[cfg(windows)]
pub fn os_to_u16(s: &OsStr) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    s.encode_wide().collect()
}

#[cfg(not(windows))]
pub fn os_to_u16(s: &OsStr) -> Vec<u16> {
    s.to_string_lossy().encode_utf16().collect()
}

#[cfg(windows)]
pub fn u16_to_path(s: &[u16]) -> PathBuf {
    use std::os::windows::ffi::OsStringExt;
    PathBuf::from(std::ffi::OsString::from_wide(s))
}

#[cfg(not(windows))]
pub fn u16_to_path(s: &[u16]) -> PathBuf {
    PathBuf::from(String::from_utf16_lossy(s))
}

fn canon(s: &str) -> String {
    s.replace('\\', "/").trim_end_matches('/').to_lowercase()
}

/// True when `path` is `folder` or inside it (`C:\Users\bob` contains `C:\Users\bob\x`, not `C:\Users\bobby`).
pub fn is_same_or_inside(path: &str, folder: &str) -> bool {
    let (p, f) = (canon(path), canon(folder));
    if f.is_empty() {
        return false;
    }
    p == f || (p.len() > f.len() && p.starts_with(&f) && p.as_bytes()[f.len()] == b'/')
}

/// Another Windows user's profile folder (`excludes`), or another user's folder inside
/// `<drive>\$Recycle.Bin`. The same rule the C# side applies (`InteractiveUser`).
pub fn is_other_users_data(path: &str, excludes: &[String], own_sid: &str) -> bool {
    if excludes.iter().any(|e| is_same_or_inside(path, e)) {
        return true;
    }
    let canonical = path.replace('\\', "/");
    let mut parts = canonical.split('/').filter(|p| !p.is_empty());
    let (Some(_drive), Some(bin), Some(owner)) = (parts.next(), parts.next(), parts.next()) else {
        return false;
    };
    bin.eq_ignore_ascii_case("$Recycle.Bin")
        && owner.len() >= 4
        && owner[..4].eq_ignore_ascii_case("S-1-")
        && !owner.eq_ignore_ascii_case(own_sid)
}

/// Last extension of a file name, lower-cased, like `Path.GetExtension(...).ToLowerInvariant()`.
/// Empty when there is none (`"file"`, `"file."`).
pub fn extension_lower(name: &[u16]) -> String {
    match name.iter().rposition(|&c| c == b'.' as u16) {
        Some(i) if i + 1 < name.len() => String::from_utf16_lossy(&name[i..]).to_lowercase(),
        _ => String::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn inside_checks_whole_components() {
        assert!(is_same_or_inside(r"C:\Users\bob", r"C:\Users\bob"));
        assert!(is_same_or_inside(r"C:\Users\bob\Docs", r"c:\users\BOB\"));
        assert!(!is_same_or_inside(r"C:\Users\bobby", r"C:\Users\bob"));
        assert!(!is_same_or_inside(r"C:\Users\bob", ""));
    }

    #[test]
    fn other_users_data() {
        let ex = vec![r"C:\Users\bob".to_string()];
        assert!(is_other_users_data(r"C:\Users\bob\Docs", &ex, "S-1-5-21-1"));
        assert!(!is_other_users_data(
            r"C:\Users\ana\Docs",
            &ex,
            "S-1-5-21-1"
        ));
        assert!(is_other_users_data(
            r"C:\$Recycle.Bin\S-1-5-21-2",
            &ex,
            "S-1-5-21-1"
        ));
        assert!(!is_other_users_data(
            r"C:\$Recycle.Bin\S-1-5-21-1",
            &ex,
            "S-1-5-21-1"
        ));
        assert!(!is_other_users_data(r"C:\$Recycle.Bin", &ex, "S-1-5-21-1"));
        assert!(!is_other_users_data(
            r"C:\Data\$Recycle.Bin\S-1-5-21-2",
            &ex,
            "S-1-5-21-1"
        ));
    }

    #[test]
    fn extensions() {
        let e = |s: &str| extension_lower(&s.encode_utf16().collect::<Vec<_>>());
        assert_eq!(e("Photo.JPG"), ".jpg");
        assert_eq!(e("archive.tar.GZ"), ".gz");
        assert_eq!(e(".gitignore"), ".gitignore");
        assert_eq!(e("noext"), "");
        assert_eq!(e("trailing."), "");
    }
}
