#!/usr/bin/env python3
"""RemoteMan shared-session relay.

Lets a teammate join a shared session when they can't reach the host directly (different networks,
no VPN). Both sides connect *out* to this relay, which only splices bytes: TLS runs end to end between
guest and host, pinned to the host's certificate, so the relay can't read or change anything.

Protocol (one ASCII line, then raw bytes):
  host  -> HOST <share>           relay -> OK; later CONN <id> per guest, PING now and then
  guest -> JOIN <share>           relay -> OK once spliced to the host, or NOHOST
  host  -> ACCEPT <share> <id>    relay -> OK, then this connection is the guest's pipe

Standard library only (Python 3.8+). Usage: remoteman-relay.py [--port 47120] [--bind 0.0.0.0]
"""
import argparse
import asyncio
import secrets

MAX_LINE = 200
MAX_PENDING = 200
JOIN_WAIT = 10

hosts = {}      # share -> writer of the host's control connection
pending = {}    # conn id -> (share, future that gets the host's (reader, writer))


async def read_line(reader):
    line = await asyncio.wait_for(reader.readuntil(b"\n"), 15)
    if len(line) > MAX_LINE:
        raise ValueError("line too long")
    return line.decode("ascii", "replace").strip()


async def send(writer, text):
    writer.write((text + "\n").encode("ascii"))
    await writer.drain()


async def pipe(reader, writer):
    try:
        while True:
            data = await reader.read(65536)
            if not data:
                break
            writer.write(data)
            await writer.drain()
    except (ConnectionError, asyncio.CancelledError, OSError):
        pass
    finally:
        try:
            writer.close()
        except Exception:
            pass


async def serve(reader, writer):
    keep = False
    try:
        parts = (await read_line(reader)).split()
        if len(parts) == 2 and parts[0] == "HOST":
            share = parts[1]
            old = hosts.get(share)
            if old is not None and old is not writer:
                old.close()
            hosts[share] = writer
            await send(writer, "OK")
            keep = True
            try:
                while True:
                    try:
                        data = await asyncio.wait_for(reader.read(256), 30)
                        if not data:
                            break
                    except asyncio.TimeoutError:
                        await send(writer, "PING")
            finally:
                if hosts.get(share) is writer:
                    del hosts[share]
                keep = False
        elif len(parts) == 2 and parts[0] == "JOIN":
            share = parts[1]
            host = hosts.get(share)
            if host is None or len(pending) >= MAX_PENDING:
                await send(writer, "NOHOST")
                return
            conn = secrets.token_hex(6)
            fut = asyncio.get_running_loop().create_future()
            pending[conn] = (share, fut)
            try:
                await send(host, "CONN " + conn)
                h_reader, h_writer = await asyncio.wait_for(fut, JOIN_WAIT)
            finally:
                pending.pop(conn, None)
            await send(writer, "OK")
            keep = True
            await asyncio.gather(pipe(reader, h_writer), pipe(h_reader, writer))
            keep = False
        elif len(parts) == 3 and parts[0] == "ACCEPT":
            share, conn = parts[1], parts[2]
            item = pending.get(conn)
            if item is None or item[0] != share or item[1].done():
                await send(writer, "NOCONN")
                return
            await send(writer, "OK")
            keep = True
            item[1].set_result((reader, writer))
    except Exception:
        keep = False
    finally:
        if not keep:
            try:
                writer.close()
            except Exception:
                pass


async def main():
    ap = argparse.ArgumentParser(description="RemoteMan shared-session relay")
    ap.add_argument("--port", type=int, default=47120)
    ap.add_argument("--bind", default="0.0.0.0")
    args = ap.parse_args()
    server = await asyncio.start_server(serve, args.bind, args.port)
    print(f"RemoteMan relay listening on {args.bind}:{args.port}", flush=True)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
