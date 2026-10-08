#!/usr/bin/env python3
"""Sums the wire bytes of a soak capture per client port per second.

The soak benchmark (Assets/Spike/Net/LoopbackSoak.cs) sends every client's stream from the
server's port 7778, so a capture of the loopback interface holds one UDP flow per raw client.
This script reads a classic pcap file (what `tcpdump -i lo -w soak.pcap udp port 7778`
writes), keeps the frames that belong to that port and prints

  * the per-second series: bytes down (server -> client), bytes up, and the client ports seen;
  * the per-client-port totals and rates;
  * the summary numbers the report quotes: the mean KB/s per client over the busiest window,
    and the peak second.

Wire bytes (the captured frame length) are what counts, so the result is directly comparable
with Stage A's `bw.wire.*` estimate.

Usage:
  sudo tcpdump -i lo -w /tmp/soak.pcap udp port 7778 &
  ... run the soak ...
  sudo pkill tcpdump
  python3 tools/spike-pcap.py /tmp/soak.pcap [--port 7778] [--skip-seconds 6] [--json]
"""

import argparse
import json
import struct
import sys
from collections import defaultdict


def read_pcap(path):
    """Yields (timestamp_seconds, link_type, frame_bytes). Handles both pcap byte orders."""
    with open(path, "rb") as handle:
        header = handle.read(24)
        if len(header) < 24:
            raise SystemExit(f"{path}: too short to be a pcap file")
        magic = struct.unpack("<I", header[:4])[0]
        if magic in (0xA1B2C3D4, 0xA1B23C4D):
            endian, nanosecond = "<", magic == 0xA1B23C4D
        elif magic in (0xD4C3B2A1, 0x4D3CB2A1):
            endian, nanosecond = ">", magic == 0x4D3CB2A1
        else:
            raise SystemExit(f"{path}: not a classic pcap file (magic {magic:#x}); use --pcapng?")
        link_type = struct.unpack(endian + "I", header[20:24])[0]
        while True:
            record = handle.read(16)
            if len(record) < 16:
                return
            ts_sec, ts_frac, captured, _ = struct.unpack(endian + "IIII", record)
            data = handle.read(captured)
            if len(data) < captured:
                return
            seconds = ts_sec + (ts_frac / 1e9 if nanosecond else ts_frac / 1e6)
            yield seconds, link_type, data


def udp_and_ports(frame, link_type):
    """Returns (src_port, dst_port, client_port) or None for a frame that is not IPv4 UDP."""
    offset = 0
    if link_type == 1:  # Ethernet
        if len(frame) < 14:
            return None
        ethertype = struct.unpack(">H", frame[12:14])[0]
        offset = 14
        while ethertype == 0x8100:  # VLAN
            if len(frame) < offset + 4:
                return None
            ethertype = struct.unpack(">H", frame[offset + 2:offset + 4])[0]
            offset += 4
        if ethertype != 0x0800:
            return None
    elif link_type in (113, 276):  # Linux cooked v1/v2
        base = 16 if link_type == 113 else 20
        if len(frame) < base:
            return None
        ethertype = struct.unpack(">H", frame[base - 2:base])[0]
        if ethertype != 0x0800:
            return None
        offset = base
    elif link_type in (101, 12, 14):  # raw IP
        offset = 0
    elif link_type == 0:  # BSD loopback: 4-byte family
        if len(frame) < 4:
            return None
        family = struct.unpack("<I", frame[:4])[0]
        if family not in (2,):
            return None
        offset = 4
    else:
        raise SystemExit(f"unsupported link type {link_type}; expected Ethernet (1) or Linux cooked (113/276)")

    if len(frame) < offset + 20:
        return None
    ihl = (frame[offset] & 0x0F) * 4
    if frame[offset + 9] != 17:  # protocol
        return None
    if len(frame) < offset + ihl + 8:
        return None
    src, dst = struct.unpack(">HH", frame[offset + ihl:offset + ihl + 4])
    return src, dst


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("pcap", help="the capture tcpdump wrote during the soak")
    parser.add_argument("--port", type=int, default=7778, help="the server's UDP port (default 7778)")
    parser.add_argument("--skip-seconds", type=float, default=5.0,
                        help="seconds at the start to leave out of the summary (handshake and warm-up)")
    parser.add_argument("--json", action="store_true", help="print the summary as JSON")
    args = parser.parse_args()

    per_second = defaultdict(lambda: [0, 0])          # second index -> [down bytes, up bytes]
    per_client_second = defaultdict(lambda: [0, 0])   # (client port, second) -> [down, up]
    per_client = defaultdict(lambda: [0, 0])          # client port -> [down, up]
    first = None
    frames = 0

    for seconds, link_type, frame in read_pcap(args.pcap):
        ports = udp_and_ports(frame, link_type)
        if ports is None:
            continue
        src, dst = ports
        if src != args.port and dst != args.port:
            continue
        if first is None:
            first = seconds
        index = int(seconds - first)
        down = len(frame) if src == args.port else 0
        up = len(frame) if dst == args.port else 0
        client = dst if src == args.port else src
        frames += 1
        per_second[index][0] += down
        per_second[index][1] += up
        per_client_second[(client, index)][0] += down
        per_client_second[(client, index)][1] += up
        per_client[client][0] += down
        per_client[client][1] += up

    if not frames:
        raise SystemExit(f"no UDP frames on port {args.port} in {args.pcap}")

    seconds = sorted(per_second)
    steady = [s for s in seconds if s >= args.skip_seconds]
    down = {s: per_second[s][0] for s in steady}
    per_client_rates = {}
    for client in per_client:
        rates = [per_client_second[(client, s)][0] for s in steady if (client, s) in per_client_second]
        if rates:
            per_client_rates[client] = {
                "seconds": len(rates),
                "mean_kbps": sum(rates) / len(rates) / 1024.0,
                "peak_kbps": max(rates) / 1024.0,
            }
    total_down = sum(down.values())
    window = max(1, len(steady))
    summary = {
        "port": args.port,
        "frames": frames,
        "seconds": len(seconds),
        "clients": len(per_client),
        "steady_seconds": window,
        "down_kbps_mean": total_down / window / 1024.0,
        "down_kbps_peak": max(down.values()) / 1024.0 if down else 0.0,
        "per_client_kbps_mean": (sum(p["mean_kbps"] for p in per_client_rates.values()) / len(per_client_rates))
        if per_client_rates else 0.0,
        "per_client_kbps_peak": max((p["peak_kbps"] for p in per_client_rates.values()), default=0.0),
        "per_client": {str(port): rates for port, rates in sorted(per_client_rates.items())},
    }

    if args.json:
        print(json.dumps(summary, indent=1))
        return
    print(f"{args.pcap}: {frames} UDP frames on port {args.port}, {len(per_client)} client ports, "
          f"{len(seconds)} seconds")
    print("\n  second  server->clients KB/s  clients->server KB/s  clients")
    for index in seconds:
        down_bytes, up_bytes = per_second[index]
        clients = sum(1 for (client, s) in per_client_second if s == index)
        print(f"  {index:6d}  {down_bytes / 1024.0:19.1f}  {up_bytes / 1024.0:19.1f}  {clients:7d}")
    print(f"\nsteady state (from second {args.skip_seconds:g}): "
          f"{summary['down_kbps_mean']:.1f} KB/s down a client (mean), "
          f"{summary['down_kbps_peak']:.1f} KB/s peak second, "
          f"{summary['per_client_kbps_mean']:.1f} KB/s mean per client port, "
          f"{summary['per_client_kbps_peak']:.1f} KB/s peak per client port")


if __name__ == "__main__":
    sys.exit(main())
