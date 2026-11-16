//! Binary formats returned to the C# side (all integers little-endian).
//!
//! Scan result (`WSN1`):
//! ```text
//! u32 magic = 0x314E5357 ("WSN1")   u32 node_count   u64 inaccessible   u32 extension_count
//! node_count x { u32 parent_index (0xFFFFFFFF = root)  u8 flags (1 file, 2 group)
//!                u64 size  u64 file_count  u32 name_len_utf16  u16[name_len] name }
//! extension_count x { u32 len  u16[len] extension  u64 size  u64 count }
//! ```
//! Nodes are in pre-order: a parent always comes before its children, children by size, largest first.
//!
//! Duplicates (`WSD1`):
//! ```text
//! u32 magic = 0x31445357 ("WSD1")   u32 group_count
//! group_count x { u64 size  u8[32] sha256  u32 n  u32[n] input_index }
//! ```

use crate::dupes::DupGroup;
use crate::scan::ScanResult;

pub const SCAN_MAGIC: u32 = 0x314E_5357;
pub const DUPES_MAGIC: u32 = 0x3144_5357;

fn put_u16s(out: &mut Vec<u8>, s: &[u16]) {
    out.extend_from_slice(&(s.len() as u32).to_le_bytes());
    for c in s {
        out.extend_from_slice(&c.to_le_bytes());
    }
}

pub fn encode_scan(r: &ScanResult) -> Vec<u8> {
    let mut out = Vec::with_capacity(32 + r.nodes.len() * 48);
    out.extend_from_slice(&SCAN_MAGIC.to_le_bytes());
    out.extend_from_slice(&(r.nodes.len() as u32).to_le_bytes());
    out.extend_from_slice(&r.inaccessible.to_le_bytes());
    out.extend_from_slice(&(r.extensions.len() as u32).to_le_bytes());
    for n in &r.nodes {
        out.extend_from_slice(&n.parent.to_le_bytes());
        out.push(n.flags);
        out.extend_from_slice(&n.size.to_le_bytes());
        out.extend_from_slice(&n.files.to_le_bytes());
        put_u16s(&mut out, &n.name);
    }
    for (ext, size, count) in &r.extensions {
        let units: Vec<u16> = ext.encode_utf16().collect();
        put_u16s(&mut out, &units);
        out.extend_from_slice(&size.to_le_bytes());
        out.extend_from_slice(&count.to_le_bytes());
    }
    out
}

pub fn encode_dupes(groups: &[DupGroup]) -> Vec<u8> {
    let mut out = Vec::new();
    out.extend_from_slice(&DUPES_MAGIC.to_le_bytes());
    out.extend_from_slice(&(groups.len() as u32).to_le_bytes());
    for g in groups {
        out.extend_from_slice(&g.size.to_le_bytes());
        out.extend_from_slice(&g.hash);
        out.extend_from_slice(&(g.members.len() as u32).to_le_bytes());
        for m in &g.members {
            out.extend_from_slice(&m.to_le_bytes());
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::scan::{Node, FLAG_FILE, FLAG_GROUP};

    /// The exact bytes the C# parser test (`NativeCoreTests`) is written against.
    pub fn sample_scan() -> ScanResult {
        ScanResult {
            nodes: vec![
                Node {
                    parent: u32::MAX,
                    flags: 0,
                    size: 30,
                    files: 3,
                    name: vec![],
                },
                Node {
                    parent: 0,
                    flags: 0,
                    size: 20,
                    files: 2,
                    name: "dir".encode_utf16().collect(),
                },
                Node {
                    parent: 1,
                    flags: FLAG_FILE,
                    size: 20,
                    files: 1,
                    name: "a.iso".encode_utf16().collect(),
                },
                Node {
                    parent: 0,
                    flags: FLAG_FILE | FLAG_GROUP,
                    size: 10,
                    files: 1,
                    name: vec![],
                },
            ],
            extensions: vec![(".iso".into(), 20, 1), ("(no extension)".into(), 10, 1)],
            inaccessible: 2,
        }
    }

    #[test]
    fn scan_layout_is_stable() {
        let b = encode_scan(&sample_scan());
        assert_eq!(&b[0..4], b"WSN1");
        assert_eq!(u32::from_le_bytes(b[4..8].try_into().unwrap()), 4);
        assert_eq!(u64::from_le_bytes(b[8..16].try_into().unwrap()), 2);
        assert_eq!(u32::from_le_bytes(b[16..20].try_into().unwrap()), 2);
        // root: parent, flags, size, files, name_len = 4 + 1 + 8 + 8 + 4 bytes
        assert_eq!(u32::from_le_bytes(b[20..24].try_into().unwrap()), u32::MAX);
        assert_eq!(b[24], 0);
        assert_eq!(u64::from_le_bytes(b[25..33].try_into().unwrap()), 30);
        assert_eq!(u64::from_le_bytes(b[33..41].try_into().unwrap()), 3);
        assert_eq!(u32::from_le_bytes(b[41..45].try_into().unwrap()), 0);
        // second node starts at 45 with parent 0 and a 3-unit name
        assert_eq!(u32::from_le_bytes(b[45..49].try_into().unwrap()), 0);
        assert_eq!(u32::from_le_bytes(b[66..70].try_into().unwrap()), 3);
        assert_eq!(&b[70..76], &[b'd', 0, b'i', 0, b'r', 0]);
    }

    #[test]
    fn dupes_layout_is_stable() {
        let g = vec![DupGroup {
            size: 7,
            hash: [9; 32],
            members: vec![3, 5],
        }];
        let b = encode_dupes(&g);
        assert_eq!(&b[0..4], b"WSD1");
        assert_eq!(u32::from_le_bytes(b[4..8].try_into().unwrap()), 1);
        assert_eq!(u64::from_le_bytes(b[8..16].try_into().unwrap()), 7);
        assert_eq!(&b[16..48], &[9u8; 32]);
        assert_eq!(u32::from_le_bytes(b[48..52].try_into().unwrap()), 2);
        assert_eq!(u32::from_le_bytes(b[52..56].try_into().unwrap()), 3);
        assert_eq!(u32::from_le_bytes(b[56..60].try_into().unwrap()), 5);
        assert_eq!(b.len(), 60);
    }
}
