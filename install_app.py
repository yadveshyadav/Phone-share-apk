#!/usr/bin/env python3
"""
install_app.py - Universal Android App Installer & File Provider

Features:
  1. File Provision:
     - Accept an APK file directly, OR
     - Accept an embedded carrier file (JPG, PNG, PDF, etc. created by embed_apk.py)
       and automatically extract the APK with SHA-256 verification, OR
     - Auto-detect the project's built Release/Debug APK in the workspace.
  2. Delivery & Installation Options:
     - Wi-Fi / QR Code Server: Host a lightweight download page with a QR code in the
       console so any Android phone on the same Wi-Fi can scan, download, and install.
     - ADB Direct Install: Auto-detects adb.exe (PATH or Android SDK) and pushes the APK
       directly to connected Android devices.
     - Export to Folder: Copy the verified APK to Desktop, Downloads, or custom folder.
     - Inspect & Verify: Calculate SHA-256 and confirm package details.
"""

import argparse
import datetime
import hashlib
import http.server
import json
import os
import shutil
import socket
import struct
import subprocess
import sys
import threading
import time
import urllib.parse

# Ensure UTF-8 output on Windows terminal
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


def find_adb_executable() -> str | None:
    """Locate adb executable from PATH or common Android SDK directories."""
    which_adb = shutil.which("adb")
    if which_adb:
        return which_adb

    candidates = [
        os.path.expandvars(r"%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe"),
        os.path.expandvars(r"%PROGRAMFILES%\Android\android-sdk\platform-tools\adb.exe"),
        os.path.expandvars(r"%PROGRAMFILES(X86)%\Android\android-sdk\platform-tools\adb.exe"),
        r"C:\Android\platform-tools\adb.exe",
        os.path.expanduser("~/AppData/Local/Android/Sdk/platform-tools/adb.exe"),
        os.path.expanduser("~/Android/Sdk/platform-tools/adb"),
    ]
    for c in candidates:
        if os.path.isfile(c):
            return os.path.normpath(c)
    return None


def is_apk_signed(apk_path: str) -> bool:
    """Quick check whether an APK contains signatures in META-INF."""
    try:
        import zipfile
        with zipfile.ZipFile(apk_path, "r") as z:
            names = [n.upper() for n in z.namelist()]
            return any(n.startswith("META-INF/") and (n.endswith(".RSA") or n.endswith(".DSA") or n.endswith(".EC") or n.endswith(".SF")) for n in names)
    except Exception:
        return False


def find_workspace_apks(start_dir: str = ".") -> list[dict]:
    """Find valid signed APKs in workspace, sorted by priority (Release > Publish > Debug)."""
    apks = []
    for root, _, files in os.walk(start_dir):
        for f in files:
            if f.lower().endswith(".apk"):
                full_path = os.path.normpath(os.path.join(root, f))
                if not is_apk_signed(full_path):
                    continue
                size = os.path.getsize(full_path)
                mtime = os.path.getmtime(full_path)
                is_signed_name = "-signed" in f.lower() or f.lower() == "calculator.apk"
                is_release = "release" in full_path.lower()
                is_arm64 = "android-arm64" in full_path.lower()
                priority = (4 if is_arm64 else 0) + (2 if is_release else 0) + (1 if is_signed_name else 0)
                apks.append({
                    "path": full_path,
                    "filename": f,
                    "size": size,
                    "mtime": mtime,
                    "priority": priority,
                    "is_signed": True,
                    "is_release": is_release,
                })

    # Sort by priority desc, then newest modification time desc
    apks.sort(key=lambda x: (x["priority"], x["mtime"]), reverse=True)
    return apks


def extract_from_carrier(carrier_path: str, temp_dir: str = None) -> tuple[str, str, int] | None:
    """
    Check if a file is an embedded carrier file (created by embed_apk.py)
    and extract the APK. Returns (extracted_apk_path, original_filename, apk_size) or None.
    """
    if not os.path.isfile(carrier_path):
        return None

    with open(carrier_path, "rb") as f:
        carrier_data = f.read()

    meta = None
    apk_bytes = None

    # 1. Try Universal Trailer
    footer_len = len(MAGIC_END) + 8
    if len(carrier_data) >= footer_len + len(MAGIC_START) and carrier_data.endswith(MAGIC_END):
        end_offset = len(carrier_data) - len(MAGIC_END)
        total_len = struct.unpack(">Q", carrier_data[end_offset - 8 : end_offset])[0]
        container_start = end_offset - 8 - total_len
        if 0 <= container_start < len(carrier_data):
            container = carrier_data[container_start : end_offset - 8]
            if container.startswith(MAGIC_START):
                meta_start = len(MAGIC_START)
                meta_len = struct.unpack(">I", container[meta_start : meta_start + 4])[0]
                meta_json_start = meta_start + 4
                meta_json_end = meta_json_start + meta_len
                try:
                    meta = json.loads(container[meta_json_start:meta_json_end].decode("utf-8"))
                    apk_bytes = container[meta_json_end:]
                except Exception:
                    pass

    # 2. Try PNG Ancillary Chunk
    if apk_bytes is None and carrier_data.startswith(PNG_SIGNATURE):
        offset = 8
        while offset < len(carrier_data):
            if offset + 8 > len(carrier_data):
                break
            chunk_len = struct.unpack(">I", carrier_data[offset : offset + 4])[0]
            chunk_type = carrier_data[offset + 4 : offset + 8]
            chunk_data_start = offset + 8
            chunk_data_end = chunk_data_start + chunk_len
            if chunk_type == PNG_CHUNK_TYPE:
                chunk_data = carrier_data[chunk_data_start:chunk_data_end]
                if len(chunk_data) >= 4:
                    meta_len = struct.unpack(">I", chunk_data[:4])[0]
                    if len(chunk_data) >= 4 + meta_len:
                        try:
                            meta = json.loads(chunk_data[4 : 4 + meta_len].decode("utf-8"))
                            apk_bytes = chunk_data[4 + meta_len :]
                            break
                        except Exception:
                            pass
            offset = chunk_data_end + 4

    if apk_bytes is None or meta is None:
        return None

    # Check hash
    expected_hash = meta.get("sha256")
    actual_hash = compute_sha256(apk_bytes)
    if expected_hash and actual_hash != expected_hash:
        print(f"[!] Warning: Carrier embedded SHA-256 mismatch!")

    target_name = meta.get("filename", "extracted_app.apk")
    if temp_dir is None:
        temp_dir = os.path.dirname(os.path.abspath(carrier_path))

    extracted_path = os.path.join(temp_dir, target_name)
    if os.path.exists(extracted_path) and os.path.abspath(extracted_path) == os.path.abspath(carrier_path):
        extracted_path = os.path.join(temp_dir, f"extracted_{target_name}")

    with open(extracted_path, "wb") as f:
        f.write(apk_bytes)

    return extracted_path, target_name, len(apk_bytes)


def resolve_target_file(input_path: str | None = None) -> tuple[str, bool]:
    """
    Resolve and provide the APK file.
    If input_path is provided:
      - If it's already an APK, return it.
      - If it's a carrier file with embedded APK, extract it and return extracted APK path.
    If input_path is None:
      - Automatically find the best workspace APK.
    Returns: (apk_path, is_extracted_from_carrier)
    """
    if input_path:
        cleaned_path = input_path.strip().strip('"').strip("'")
        if not os.path.isfile(cleaned_path):
            raise FileNotFoundError(f"File not found: {cleaned_path}")

        # Check if it's an APK directly
        if cleaned_path.lower().endswith(".apk"):
            return os.path.abspath(cleaned_path), False

        # Attempt extraction from carrier
        print(f"[*] Checking if '{os.path.basename(cleaned_path)}' contains an embedded APK...")
        carrier_res = extract_from_carrier(cleaned_path)
        if carrier_res:
            ext_path, orig_name, size = carrier_res
            print(f"[+] Successfully extracted embedded APK: {orig_name} ({size:,} bytes)")
            return ext_path, True
        else:
            raise ValueError(f"File '{cleaned_path}' is neither an APK nor contains embedded APK metadata.")

    # Search workspace for built APKs
    found = find_workspace_apks()
    if not found:
        raise FileNotFoundError(
            "No built APK found in workspace. Please build the project or provide a file path."
        )

    best_apk = found[0]["path"]
    return os.path.abspath(best_apk), False


def get_local_ip() -> str:
    """Get the primary local IPv4 address."""
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(1.0)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "127.0.0.1"


def print_qr_code(url: str):
    """Print ASCII QR code to console."""
    try:
        import qrcode
        qr = qrcode.QRCode(
            version=1,
            error_correction=qrcode.constants.ERROR_CORRECT_L,
            box_size=1,
            border=2,
        )
        qr.add_data(url)
        qr.make(fit=True)
        print("\n" + "=" * 54)
        print("          SCAN QR CODE WITH PHONE CAMERA            ")
        print("=" * 54)
        qr.print_ascii(invert=True)
        print("=" * 54)
    except Exception:
        # Fallback if qrcode module fails
        print(f"\n[QR code library unavailable. Direct link: {url}]")


class ApkDownloadHandler(http.server.BaseHTTPRequestHandler):
    """Serves the APK file and a mobile-friendly download page."""
    apk_file_path: str = ""
    apk_file_name: str = ""
    apk_file_size: int = 0
    apk_sha256: str = ""

    def log_message(self, format, *args):
        # Clean logging
        sys.stdout.write(f"[{datetime.datetime.now().strftime('%H:%M:%S')}] {args[0]} - {args[1]}\n")
        sys.stdout.flush()

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path

        if path in ("/download", f"/{self.apk_file_name}"):
            # Deliver APK binary
            try:
                with open(self.apk_file_path, "rb") as f:
                    data = f.read()
                self.send_response(200)
                self.send_header("Content-Type", "application/vnd.android.package-archive")
                self.send_header("Content-Length", str(len(data)))
                self.send_header(
                    "Content-Disposition",
                    f'attachment; filename="{self.apk_file_name}"'
                )
                self.send_header("Cache-Control", "no-cache")
                self.end_headers()
                self.wfile.write(data)
                print(f"\n[+] SUCCESS: Device downloaded {self.apk_file_name} ({len(data):,} bytes)!")
            except Exception as e:
                self.send_error(500, f"Error reading APK: {e}")
            return

        # Serve responsive HTML page
        html = f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Download & Install App</title>
  <style>
    body {{
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
      background: #0f172a;
      color: #f8fafc;
      margin: 0;
      padding: 24px;
      display: flex;
      justify-content: center;
      align-items: center;
      min-height: 90vh;
    }}
    .card {{
      background: #1e293b;
      border-radius: 16px;
      max-width: 440px;
      width: 100%;
      padding: 32px 24px;
      box-shadow: 0 10px 25px -5px rgba(0, 0, 0, 0.4);
      border: 1px solid #334155;
      text-align: center;
    }}
    .icon {{
      font-size: 52px;
      margin-bottom: 12px;
    }}
    h1 {{
      font-size: 24px;
      margin: 0 0 8px 0;
      color: #ffffff;
    }}
    .subtitle {{
      color: #94a3b8;
      font-size: 14px;
      margin-bottom: 24px;
      word-break: break-all;
    }}
    .btn {{
      display: block;
      background: #2563eb;
      color: #ffffff;
      text-decoration: none;
      font-weight: 600;
      font-size: 17px;
      padding: 16px;
      border-radius: 12px;
      margin-bottom: 20px;
      box-shadow: 0 4px 14px 0 rgba(37, 99, 235, 0.4);
      transition: background 0.2s;
    }}
    .btn:hover, .btn:active {{
      background: #1d4ed8;
    }}
    .info {{
      background: #0f172a;
      border-radius: 8px;
      padding: 12px;
      font-size: 12px;
      color: #cbd5e1;
      text-align: left;
      line-height: 1.6;
      border: 1px solid #334155;
    }}
    .info b {{
      color: #38bdf8;
    }}
    .steps {{
      text-align: left;
      margin-top: 20px;
      font-size: 13px;
      color: #94a3b8;
      border-top: 1px solid #334155;
      padding-top: 16px;
    }}
    .steps ol {{
      padding-left: 20px;
      margin: 8px 0;
    }}
  </style>
</head>
<body>
  <div class="card">
    <div class="icon">📱</div>
    <h1>Install Application</h1>
    <div class="subtitle">{self.apk_file_name}</div>
    <a href="/download" class="btn">⬇️ Download & Install APK</a>
    <div class="info">
      <div><b>Size:</b> {self.apk_file_size / 1024 / 1024:.2f} MB ({self.apk_file_size:,} bytes)</div>
      <div><b>SHA-256:</b> {self.apk_sha256[:16]}...</div>
    </div>
    <div class="steps">
      <b>Installation Instructions:</b>
      <ol>
        <li>Tap the download button above.</li>
        <li>Open the downloaded APK from your notifications or Downloads.</li>
        <li>If prompted, allow <i>"Install unknown apps"</i> for your browser.</li>
        <li>Tap <b>Install</b> to complete setup.</li>
      </ol>
    </div>
  </div>
</body>
</html>"""
        encoded = html.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)


def serve_apk_over_wifi(apk_path: str, port: int = 8080):
    """Start local web server for phone installation and display QR code."""
    filename = os.path.basename(apk_path)
    size = os.path.getsize(apk_path)
    with open(apk_path, "rb") as f:
        apk_hash = compute_sha256(f.read())

    ApkDownloadHandler.apk_file_path = apk_path
    ApkDownloadHandler.apk_file_name = filename
    ApkDownloadHandler.apk_file_size = size
    ApkDownloadHandler.apk_sha256 = apk_hash

    # Find free port if requested port is taken
    active_port = port
    server = None
    for p in range(port, port + 20):
        try:
            server = http.server.ThreadingHTTPServer(("0.0.0.0", p), ApkDownloadHandler)
            active_port = p
            break
        except OSError:
            continue

    if server is None:
        print(f"[!] Could not bind HTTP server on ports {port}-{port+20}.")
        return

    local_ip = get_local_ip()
    url = f"http://{local_ip}:{active_port}"
    download_url = f"{url}/download"

    print("\n" + "=" * 60)
    print("      WI-FI / OVER-THE-AIR APK INSTALLER SERVER      ")
    print("=" * 60)
    print(f"APK File:      {filename} ({size:,} bytes)")
    print(f"Download URL:  {download_url}")
    print(f"Web Portal:    {url}")
    print("=" * 60)

    print_qr_code(url)

    print("\nInstructions for your phone:")
    print("  1. Make sure your phone is connected to the same Wi-Fi / local network.")
    print("  2. Open Camera or QR scanner on your phone and scan the code above.")
    print(f"     (Or open this link in your phone's browser: {url})")
    print("  3. Tap 'Download & Install APK'.")
    print("\n[Press Ctrl+C to stop the server when done]")

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n[*] Web server stopped.")
    finally:
        server.server_close()


def install_via_adb(apk_path: str):
    """Install APK directly to USB or wireless connected Android device via ADB."""
    adb_path = find_adb_executable()
    if not adb_path:
        print("\n[!] ADB executable not found.")
        print("  Please ensure Android SDK platform-tools is installed or adb is on your PATH.")
        return False

    print(f"\n[*] Using ADB: {adb_path}")
    print("[*] Checking for connected Android devices...")

    try:
        proc = subprocess.run([adb_path, "devices"], capture_output=True, text=True, check=True)
    except Exception as e:
        print(f"[!] Error running ADB: {e}")
        return False

    lines = proc.stdout.strip().splitlines()
    devices = []
    for line in lines[1:]:
        line = line.strip()
        if not line:
            continue
        parts = line.split()
        if len(parts) >= 2 and parts[1] == "device":
            devices.append(parts[0])

    if not devices:
        print("\n[-] No active Android device detected.")
        print("  Please:")
        print("  1. Connect your Android phone via USB cable.")
        print("  2. Enable 'Developer Options' and 'USB Debugging' on your phone.")
        print("  3. Approve the 'Allow USB debugging?' prompt on your phone screen.")
        print("\nAlternatively, you can choose the Wi-Fi / QR Code method to install without USB.")
        return False

    print(f"[+] Found {len(devices)} device(s): {', '.join(devices)}")
    for dev in devices:
        print(f"\n[*] Installing {os.path.basename(apk_path)} to device [{dev}]...")
        # Use -r (reinstall) and -d (allow version downgrade)
        install_res = subprocess.run(
            [adb_path, "-s", dev, "install", "-r", "-d", apk_path],
            capture_output=True,
            text=True
        )
        print(install_res.stdout.strip())
        if install_res.stderr:
            print(install_res.stderr.strip())

        if "Success" in install_res.stdout:
            print(f"[+] Installation to [{dev}] succeeded!")
        else:
            print(f"[!] Installation to [{dev}] reported errors.")
    return True


def export_apk(apk_path: str, dest_folder: str = None):
    """Copy APK to Desktop or chosen directory."""
    if dest_folder is None:
        desktop = os.path.join(os.path.expanduser("~"), "Desktop")
        dest_folder = desktop if os.path.isdir(desktop) else "."

    os.makedirs(dest_folder, exist_ok=True)
    dest_file = os.path.join(dest_folder, os.path.basename(apk_path))
    shutil.copy2(apk_path, dest_file)
    print(f"\n[+] APK successfully copied to:")
    print(f"  {os.path.abspath(dest_file)} ({os.path.getsize(dest_file):,} bytes)")


def print_apk_info(apk_path: str):
    """Print APK file details and checksums."""
    filename = os.path.basename(apk_path)
    size = os.path.getsize(apk_path)
    mtime = datetime.datetime.fromtimestamp(os.path.getmtime(apk_path)).strftime('%Y-%m-%d %H:%M:%S')

    with open(apk_path, "rb") as f:
        data = f.read()
    sha256 = compute_sha256(data)

    print("\n" + "=" * 54)
    print("               APK FILE INFORMATION                 ")
    print("=" * 54)
    print(f"File Name:     {filename}")
    print(f"Full Path:     {os.path.abspath(apk_path)}")
    print(f"File Size:     {size:,} bytes ({size / 1024 / 1024:.2f} MB)")
    print(f"Last Modified: {mtime}")
    print(f"SHA-256 Hash:  {sha256}")
    print("=" * 54)


def interactive_wizard():
    """Interactive console wizard for selecting file and installation method."""
    print("=====================================================")
    print("       Android App Installer & File Provider         ")
    print("=====================================================")

    # Step 1: File Selection
    workspace_apks = find_workspace_apks()
    default_apk = workspace_apks[0]["path"] if workspace_apks else None

    print("\n[STEP 1: SELECT OR PROVIDE FILE]")
    if default_apk:
        print(f"Default project APK detected:")
        print(f"  -> {os.path.basename(default_apk)} ({os.path.getsize(default_apk):,} bytes)")
        print("\nOptions:")
        print("  1. Use default project APK (Press Enter)")
        print("  2. Provide a custom file (Drag & drop an .apk or an embedded carrier image/PDF)")
        user_file = input("\nEnter file path or press Enter for default:\n> ").strip().strip('"').strip("'")
        if not user_file:
            user_file = default_apk
    else:
        user_file = input("\nEnter file path (Drag & drop an .apk or an embedded carrier file):\n> ").strip().strip('"').strip("'")

    try:
        apk_path, was_extracted = resolve_target_file(user_file)
    except Exception as e:
        print(f"\n[!] Error resolving file: {e}")
        return

    print_apk_info(apk_path)

    # Step 2: Installation action
    print("\n[STEP 2: CHOOSE INSTALLATION / DELIVERY METHOD]")
    print("  1. Wi-Fi / QR Code Web Installer (Install directly on phone via local Wi-Fi)")
    print("  2. USB / ADB Direct Install (Install directly to connected Android phone)")
    print("  3. Export APK to Desktop")
    print("  4. Exit")

    choice = input("\nSelect an option [1-4] (default 1): ").strip() or "1"

    if choice == "1":
        port_input = input("Enter port (press Enter for 8080): ").strip()
        port = int(port_input) if port_input.isdigit() else 8080
        serve_apk_over_wifi(apk_path, port)
    elif choice == "2":
        install_via_adb(apk_path)
    elif choice == "3":
        export_apk(apk_path)
    elif choice == "4":
        print("Exiting.")
    else:
        print("Invalid selection.")


def main():
    parser = argparse.ArgumentParser(
        description="Universal Android App Installer & File Provider with Wi-Fi QR Code and ADB direct install."
    )
    parser.add_argument(
        "-f", "--file",
        help="Path to an .apk file OR an embedded carrier file (JPG, PNG, PDF, etc.)"
    )
    parser.add_argument(
        "--wifi", action="store_true",
        help="Host Wi-Fi web server with QR code for phone installation"
    )
    parser.add_argument(
        "--port", type=int, default=8080,
        help="Port for the Wi-Fi web server (default: 8080)"
    )
    parser.add_argument(
        "--adb", action="store_true",
        help="Directly install to connected device using ADB"
    )
    parser.add_argument(
        "--export", metavar="DEST_FOLDER", nargs="?", const="Desktop",
        help="Export/copy the verified APK to Desktop or specified folder"
    )
    parser.add_argument(
        "--info", action="store_true",
        help="Display file details and SHA-256 checksum"
    )

    args = parser.parse_args()

    # If no flags passed, run interactive wizard
    if not (args.wifi or args.adb or args.export or args.info):
        interactive_wizard()
        return

    apk_path, _ = resolve_target_file(args.file)

    if args.info:
        print_apk_info(apk_path)

    if args.export:
        dest = None if args.export == "Desktop" else args.export
        export_apk(apk_path, dest)

    if args.adb:
        install_via_adb(apk_path)

    if args.wifi:
        serve_apk_over_wifi(apk_path, args.port)


if __name__ == "__main__":
    main()

