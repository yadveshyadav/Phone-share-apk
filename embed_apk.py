#!/usr/bin/env python3
"""
embed_apk.py - Embed an Android APK into any file's metadata or carrier payload,
and extract it back anytime with 100% SHA-256 integrity verification.

Supported carrier files:
  - PNG images: Native PNG ancillary metadata chunk ('apKd')
  - JPEG / JPG images: Appended metadata container (preserves JPEG rendering)
  - PDF, MP4, MP3, ZIP, WebP, or any file format: Universal metadata container
"""

import argparse
import datetime
import hashlib
import json
import os
import struct
import sys
import zlib

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

MAGIC_START = b"===APK_METADATA_START==="
MAGIC_END   = b"===APK_METADATA_END==="
PNG_CHUNK_TYPE = b"apKd"
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def compute_sha256(data: bytes) -> str:
    """Compute SHA-256 hash of byte buffer."""
    return hashlib.sha256(data).hexdigest()


def is_apk_signed(apk_path: str) -> bool:
    """Quick check whether an APK contains signatures in META-INF."""
    try:
        import zipfile
        with zipfile.ZipFile(apk_path, "r") as z:
            names = [n.upper() for n in z.namelist()]
            return any(n.startswith("META-INF/") and (n.endswith(".RSA") or n.endswith(".DSA") or n.endswith(".EC") or n.endswith(".SF")) for n in names)
    except Exception:
        return False


def find_default_apks(start_dir: str = ".") -> list[str]:
    """Find valid signed APK files in workspace, sorted by priority (Release > Debug)."""
    candidates = []
    for root, _, files in os.walk(start_dir):
        for f in files:
            if f.lower().endswith(".apk"):
                full_path = os.path.normpath(os.path.join(root, f))
                if not is_apk_signed(full_path):
                    continue
                is_release = "release" in full_path.lower()
                is_arm64 = "android-arm64" in full_path.lower()
                is_signed_name = "-signed" in f.lower() or f.lower() == "calculator.apk"
                priority = (4 if is_arm64 else 0) + (2 if is_release else 0) + (1 if is_signed_name else 0)
                mtime = os.path.getmtime(full_path)
                candidates.append((priority, mtime, full_path))

    candidates.sort(key=lambda x: (x[0], x[1]), reverse=True)
    return [c[2] for c in candidates]


def embed_into_png_chunk(carrier_data: bytes, apk_data: bytes, apk_name: str) -> bytes:
    """
    Embed APK into a native PNG ancillary chunk ('apKd') inserted right before IEND.
    Conforms to the official PNG specification:
      - 'a' (lowercase): Ancillary chunk (viewers ignore if unknown)
      - 'p' (lowercase): Private chunk
      - 'K' (uppercase): Conforming chunk
      - 'd' (lowercase): Safe-to-copy chunk
    """
    if not carrier_data.startswith(PNG_SIGNATURE):
        raise ValueError("File is not a valid PNG file.")

    iend_idx = carrier_data.rfind(b"IEND")
    if iend_idx == -1 or iend_idx < 4:
        raise ValueError("Invalid PNG: 'IEND' chunk marker not found.")
    iend_chunk_start = iend_idx - 4

    meta = {
        "filename": apk_name,
        "size": len(apk_data),
        "sha256": compute_sha256(apk_data),
        "timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "method": "png_chunk",
    }
    meta_bytes = json.dumps(meta, indent=2).encode("utf-8")
    chunk_payload = struct.pack(">I", len(meta_bytes)) + meta_bytes + apk_data

    # PNG Chunk: [4-byte Length][4-byte Type][Data][4-byte CRC32]
    chunk_len = len(chunk_payload)
    chunk_crc = zlib.crc32(PNG_CHUNK_TYPE + chunk_payload)
    custom_chunk = (
        struct.pack(">I", chunk_len)
        + PNG_CHUNK_TYPE
        + chunk_payload
        + struct.pack(">I", chunk_crc)
    )

    return carrier_data[:iend_chunk_start] + custom_chunk + carrier_data[iend_chunk_start:]


def extract_from_png_chunk(carrier_data: bytes) -> tuple[dict, bytes] | None:
    """Extract embedded APK from PNG chunk if present."""
    if not carrier_data.startswith(PNG_SIGNATURE):
        return None

    offset = 8  # Skip signature
    while offset < len(carrier_data):
        if offset + 8 > len(carrier_data):
            break
        chunk_len = struct.unpack(">I", carrier_data[offset : offset + 4])[0]
        chunk_type = carrier_data[offset + 4 : offset + 8]
        chunk_data_start = offset + 8
        chunk_data_end = chunk_data_start + chunk_len

        if chunk_type == PNG_CHUNK_TYPE:
            chunk_data = carrier_data[chunk_data_start:chunk_data_end]
            if len(chunk_data) < 4:
                return None
            meta_len = struct.unpack(">I", chunk_data[:4])[0]
            if len(chunk_data) < 4 + meta_len:
                return None
            meta = json.loads(chunk_data[4 : 4 + meta_len].decode("utf-8"))
            apk_bytes = chunk_data[4 + meta_len :]
            return meta, apk_bytes

        # Advance past length, type, data, and 4-byte CRC
        offset = chunk_data_end + 4

    return None


def embed_into_universal_trailer(carrier_data: bytes, apk_data: bytes, apk_name: str) -> bytes:
    """
    Embed APK into universal metadata trailer.
    Compatible with JPEG, MP4, PDF, audio, and any binary format.
    Image viewers and media players ignore trailing metadata after their EOF markers.
    """
    meta = {
        "filename": apk_name,
        "size": len(apk_data),
        "sha256": compute_sha256(apk_data),
        "timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "method": "universal_trailer",
    }
    meta_bytes = json.dumps(meta, indent=2).encode("utf-8")

    # Container structure:
    # MAGIC_START + 4-byte META_LEN + META_BYTES + APK_DATA
    container = MAGIC_START + struct.pack(">I", len(meta_bytes)) + meta_bytes + apk_data
    total_len = len(container)

    # Trailer ends with: [8-byte total_len][MAGIC_END]
    trailer = container + struct.pack(">Q", total_len) + MAGIC_END
    return carrier_data + trailer


def extract_from_universal_trailer(carrier_data: bytes) -> tuple[dict, bytes, int] | None:
    """
    Extract embedded APK from universal trailer if present.
    Returns (meta_dict, apk_bytes, carrier_original_length) or None.
    """
    footer_len = len(MAGIC_END) + 8  # MAGIC_END + 8 bytes total_len
    if len(carrier_data) < footer_len + len(MAGIC_START):
        return None

    if not carrier_data.endswith(MAGIC_END):
        return None

    end_offset = len(carrier_data) - len(MAGIC_END)
    total_len = struct.unpack(">Q", carrier_data[end_offset - 8 : end_offset])[0]

    container_start = end_offset - 8 - total_len
    if container_start < 0 or container_start >= len(carrier_data):
        return None

    container = carrier_data[container_start : end_offset - 8]
    if not container.startswith(MAGIC_START):
        return None

    meta_start = len(MAGIC_START)
    meta_len = struct.unpack(">I", container[meta_start : meta_start + 4])[0]
    meta_json_start = meta_start + 4
    meta_json_end = meta_json_start + meta_len
    meta = json.loads(container[meta_json_start:meta_json_end].decode("utf-8"))
    apk_bytes = container[meta_json_end:]

    return meta, apk_bytes, container_start


def inspect_file(file_path: str):
    """Inspect carrier file and print embedded APK metadata."""
    if not os.path.isfile(file_path):
        print(f"[!] Error: File not found: {file_path}")
        return False

    with open(file_path, "rb") as f:
        data = f.read()

    # Check universal trailer first
    trailer_res = extract_from_universal_trailer(data)
    if trailer_res:
        meta, apk_bytes, original_len = trailer_res
        print("\n=== Embedded APK Information ===")
        print(f"Carrier File:      {os.path.basename(file_path)} ({len(data):,} bytes)")
        print(f"Original Carrier:  {original_len:,} bytes")
        print(f"Storage Method:    Universal Metadata Container (Trailer)")
        print(f"APK Filename:      {meta.get('filename')}")
        print(f"APK Size:          {len(apk_bytes):,} bytes ({len(apk_bytes) / 1024 / 1024:.2f} MB)")
        print(f"Embedded Hash:     {meta.get('sha256')}")
        actual_hash = compute_sha256(apk_bytes)
        print(f"Actual Hash:       {actual_hash}")
        print(f"Integrity Valid:   {'[OK] Valid (Matches perfectly)' if actual_hash == meta.get('sha256') else '[FAIL] MISMATCH'}")
        print(f"Embedded At (UTC): {meta.get('timestamp')}")
        print("================================")
        return True

    # Check PNG chunk
    png_res = extract_from_png_chunk(data)
    if png_res:
        meta, apk_bytes = png_res
        print("\n=== Embedded APK Information ===")
        print(f"Carrier File:      {os.path.basename(file_path)} ({len(data):,} bytes)")
        print(f"Storage Method:    PNG Ancillary Metadata Chunk ('apKd')")
        print(f"APK Filename:      {meta.get('filename')}")
        print(f"APK Size:          {len(apk_bytes):,} bytes ({len(apk_bytes) / 1024 / 1024:.2f} MB)")
        print(f"Embedded Hash:     {meta.get('sha256')}")
        actual_hash = compute_sha256(apk_bytes)
        print(f"Actual Hash:       {actual_hash}")
        print(f"Integrity Valid:   {'[OK] Valid (Matches perfectly)' if actual_hash == meta.get('sha256') else '[FAIL] MISMATCH'}")
        print(f"Embedded At (UTC): {meta.get('timestamp')}")
        print("================================")
        return True

    print(f"\n[-] No embedded APK found in {file_path}")
    return False


def embed_apk(carrier_path: str, apk_path: str, output_path: str = None, mode: str = "auto"):
    """Embed APK into carrier file."""
    if not os.path.isfile(carrier_path):
        raise FileNotFoundError(f"Carrier file not found: {carrier_path}")
    if not os.path.isfile(apk_path):
        raise FileNotFoundError(f"APK file not found: {apk_path}")

    with open(carrier_path, "rb") as f:
        carrier_data = f.read()

    with open(apk_path, "rb") as f:
        apk_data = f.read()

    apk_name = os.path.basename(apk_path)
    carrier_ext = os.path.splitext(carrier_path)[1].lower()

    if output_path is None:
        base, ext = os.path.splitext(carrier_path)
        output_path = f"{base}_embedded{ext}"

    # Determine method
    if mode == "chunk" or (mode == "auto" and carrier_ext == ".png" and carrier_data.startswith(PNG_SIGNATURE)):
        print(f"[*] Embedding using PNG Metadata Chunk ('{PNG_CHUNK_TYPE.decode()}')...")
        out_data = embed_into_png_chunk(carrier_data, apk_data, apk_name)
    else:
        print(f"[*] Embedding using Universal Metadata Container...")
        out_data = embed_into_universal_trailer(carrier_data, apk_data, apk_name)

    with open(output_path, "wb") as f:
        f.write(out_data)

    print("\n[+] Successfully embedded APK!")
    print(f"  Carrier File:  {carrier_path} ({len(carrier_data):,} bytes)")
    print(f"  APK Embedded:  {apk_path} ({len(apk_data):,} bytes)")
    print(f"  APK SHA-256:   {compute_sha256(apk_data)}")
    print(f"  Output File:   {output_path} ({len(out_data):,} bytes)")


def extract_apk(carrier_path: str, output_path: str = None):
    """Extract embedded APK from carrier file."""
    if not os.path.isfile(carrier_path):
        raise FileNotFoundError(f"Carrier file not found: {carrier_path}")

    with open(carrier_path, "rb") as f:
        carrier_data = f.read()

    # Check universal trailer first
    trailer_res = extract_from_universal_trailer(carrier_data)
    if trailer_res:
        meta, apk_bytes, _ = trailer_res
    else:
        # Check PNG chunk
        png_res = extract_from_png_chunk(carrier_data)
        if png_res:
            meta, apk_bytes = png_res
        else:
            raise ValueError(f"No embedded APK found inside '{carrier_path}'.")

    expected_hash = meta.get("sha256")
    actual_hash = compute_sha256(apk_bytes)

    if actual_hash != expected_hash:
        print(f"[!] Warning: Hash mismatch! Embedded: {expected_hash}, Actual: {actual_hash}")
    else:
        print(f"[OK] SHA-256 integrity check passed!")

    if output_path is None:
        target_name = meta.get("filename", "extracted_app.apk")
        output_dir = os.path.dirname(carrier_path)
        output_path = os.path.join(output_dir, target_name)
        if os.path.exists(output_path):
            output_path = os.path.join(output_dir, f"extracted_{target_name}")

    with open(output_path, "wb") as f:
        f.write(apk_bytes)

    print(f"\n[+] Successfully extracted APK to:")
    print(f"  {output_path} ({len(apk_bytes):,} bytes)")


def strip_apk(carrier_path: str, output_path: str = None):
    """Remove embedded APK metadata and restore original carrier file."""
    if not os.path.isfile(carrier_path):
        raise FileNotFoundError(f"Carrier file not found: {carrier_path}")

    with open(carrier_path, "rb") as f:
        carrier_data = f.read()

    trailer_res = extract_from_universal_trailer(carrier_data)
    if trailer_res:
        _, _, original_len = trailer_res
        clean_data = carrier_data[:original_len]
    else:
        # Check PNG chunk
        if carrier_data.startswith(PNG_SIGNATURE):
            iend_idx = carrier_data.rfind(b"IEND")
            offset = 8
            chunks = [PNG_SIGNATURE]
            while offset < len(carrier_data):
                if offset + 8 > len(carrier_data):
                    break
                chunk_len = struct.unpack(">I", carrier_data[offset : offset + 4])[0]
                chunk_type = carrier_data[offset + 4 : offset + 8]
                full_chunk_len = 4 + 4 + chunk_len + 4
                if chunk_type != PNG_CHUNK_TYPE:
                    chunks.append(carrier_data[offset : offset + full_chunk_len])
                offset += full_chunk_len
            clean_data = b"".join(chunks)
        else:
            raise ValueError(f"No embedded APK found to strip from '{carrier_path}'.")

    if output_path is None:
        base, ext = os.path.splitext(carrier_path)
        output_path = f"{base}_restored{ext}"

    with open(output_path, "wb") as f:
        f.write(clean_data)

    print(f"\n[+] Clean carrier file restored: {output_path} ({len(clean_data):,} bytes)")


def interactive_mode():
    """Prompt user interactively if no arguments are provided."""
    print("=====================================================")
    print("        APK Metadata Embedder & Extractor            ")
    print("=====================================================")
    print("1. Embed an APK into a carrier file")
    print("2. Extract an APK from a carrier file")
    print("3. Inspect a file for embedded APK metadata")
    print("4. Strip embedded APK and restore original file")
    print("-----------------------------------------------------")

    choice = input("Select an option [1-4] (default 1): ").strip() or "1"

    if choice == "1":
        carrier = input("\n[1] Enter the Carrier file path (e.g. photo.jpg, image.png, doc.pdf):\n> ").strip().strip('"').strip("'")
        while not os.path.isfile(carrier):
            print(f"File not found: {carrier}")
            carrier = input("Enter a valid carrier file path:\n> ").strip().strip('"').strip("'")

        # Check for APKs in workspace to offer quick default
        suggested_apks = find_default_apks()
        default_apk = suggested_apks[0] if suggested_apks else ""

        if default_apk:
            print(f"\nFound existing APK in workspace: {default_apk}")
            apk = input(f"[2] Enter APK path (or press Enter to use found APK):\n> ").strip().strip('"').strip("'")
            if not apk:
                apk = default_apk
        else:
            apk = input("\n[2] Enter APK file path:\n> ").strip().strip('"').strip("'")

        while not os.path.isfile(apk):
            print(f"File not found: {apk}")
            apk = input("Enter a valid APK path:\n> ").strip().strip('"').strip("'")

        base, ext = os.path.splitext(carrier)
        suggested_output = f"{base}_embedded{ext}"
        out = input(f"\n[3] Enter output path (or press Enter for '{suggested_output}'):\n> ").strip().strip('"').strip("'")
        if not out:
            out = suggested_output

        embed_apk(carrier, apk, out)

    elif choice == "2":
        carrier = input("\nEnter carrier file to extract APK from:\n> ").strip().strip('"').strip("'")
        out = input("Enter output APK path (or press Enter to use original filename):\n> ").strip().strip('"').strip("'")
        extract_apk(carrier, out if out else None)

    elif choice == "3":
        carrier = input("\nEnter file path to inspect:\n> ").strip().strip('"').strip("'")
        inspect_file(carrier)

    elif choice == "4":
        carrier = input("\nEnter carrier file to strip:\n> ").strip().strip('"').strip("'")
        out = input("Enter output restored file path (or press Enter for default):\n> ").strip().strip('"').strip("'")
        strip_apk(carrier, out if out else None)

    else:
        print("Invalid choice.")


def main():
    parser = argparse.ArgumentParser(
        description="Embed an APK into any carrier file's metadata/container, and extract it back with full integrity."
    )
    subparsers = parser.add_subparsers(dest="command")

    # Embed command
    p_embed = subparsers.add_parser("embed", help="Embed an APK into a carrier file")
    p_embed.add_argument("-c", "--carrier", required=True, help="Path to the carrier file (image, pdf, mp4, etc.)")
    p_embed.add_argument("-a", "--apk", required=True, help="Path to the .apk file to embed")
    p_embed.add_argument("-o", "--output", help="Path for the output file (optional)")
    p_embed.add_argument("--mode", choices=["auto", "chunk", "trailer"], default="auto",
                         help="Embedding mode: 'chunk' (PNG chunk), 'trailer' (universal trailer), 'auto' (default)")

    # Extract command
    p_extract = subparsers.add_parser("extract", help="Extract an embedded APK from a carrier file")
    p_extract.add_argument("-c", "--carrier", required=True, help="Path to the carrier file with embedded APK")
    p_extract.add_argument("-o", "--output", help="Path to save extracted APK (optional)")

    # Info command
    p_info = subparsers.add_parser("info", help="Inspect a carrier file for embedded APK metadata")
    p_info.add_argument("-c", "--carrier", required=True, help="Path to the file to inspect")

    # Strip command
    p_strip = subparsers.add_parser("strip", help="Strip embedded APK and restore original carrier file")
    p_strip.add_argument("-c", "--carrier", required=True, help="Path to the carrier file")
    p_strip.add_argument("-o", "--output", help="Path for clean restored file (optional)")

    args = parser.parse_args()

    if not args.command:
        # No CLI arguments provided, launch interactive wizard
        interactive_mode()
        return

    if args.command == "embed":
        embed_apk(args.carrier, args.apk, args.output, args.mode)
    elif args.command == "extract":
        extract_apk(args.carrier, args.output)
    elif args.command == "info":
        inspect_file(args.carrier)
    elif args.command == "strip":
        strip_apk(args.carrier, args.output)


if __name__ == "__main__":
    main()
