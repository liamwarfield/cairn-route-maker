#!/usr/bin/env python3
"""Virtual keyboard + mouse for a headless wlroots compositor (sway).

A headless sway seat has no input devices, so Xwayland never gets keyboard
focus and XTEST events go nowhere. `serve` creates a virtual keyboard
(zwp_virtual_keyboard_v1) and pointer (zwlr_virtual_pointer_v1) on the seat
and keeps them alive; the other commands talk to it over a unix socket.

  vinput.py serve                       # needs WAYLAND_DISPLAY
  vinput.py key <keysym> [hold_s]       # Return, Escape, F6, w, space, Up ...
  vinput.py down <keysym> | up <keysym>
  vinput.py type <text>
  vinput.py move <x> <y>                # absolute, output pixels
  vinput.py rel <dx> <dy>               # relative motion (mouse look)
  vinput.py click [x y] [left|right|middle]
  vinput.py scroll <steps>              # positive = down
"""
import ctypes, os, select, socket, struct, sys, time

SOCK = os.environ.get("VINPUT_SOCK", "/tmp/cairn-fake-screen/vinput.sock")
W, H = int(os.environ.get("FAKE_SCREEN_W", 1920)), int(os.environ.get("FAKE_SCREEN_H", 1080))
BTN = {"left": 0x110, "right": 0x111, "middle": 0x112}

# --- xkb keymap + keysym -> evdev lookup -------------------------------------
xkb = ctypes.CDLL("libxkbcommon.so.0")
xkb.xkb_context_new.restype = ctypes.c_void_p
xkb.xkb_keymap_new_from_names.restype = ctypes.c_void_p
xkb.xkb_keymap_new_from_names.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int]
xkb.xkb_keymap_get_as_string.restype = ctypes.c_void_p
xkb.xkb_keymap_get_as_string.argtypes = [ctypes.c_void_p, ctypes.c_int]
xkb.xkb_keysym_from_name.restype = ctypes.c_uint32
xkb.xkb_keysym_from_name.argtypes = [ctypes.c_char_p, ctypes.c_int]
xkb.xkb_keymap_key_get_syms_by_level.argtypes = [
    ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32,
    ctypes.POINTER(ctypes.POINTER(ctypes.c_uint32))]

def keymap():
    km = xkb.xkb_keymap_new_from_names(xkb.xkb_context_new(0), None, 0)
    return km, ctypes.string_at(xkb.xkb_keymap_get_as_string(km, 1))

def lookup(km, name):
    """keysym name -> (evdev code, needs_shift)"""
    aliases = {" ": "space", "\n": "Return", "\t": "Tab", "enter": "Return", "esc": "Escape"}
    name = aliases.get(name, aliases.get(name.lower(), name))
    sym = xkb.xkb_keysym_from_name(name.encode(), 0)
    if not sym:
        raise ValueError(f"unknown key {name!r}")
    syms = ctypes.POINTER(ctypes.c_uint32)()
    for level in (0, 1):
        for kc in range(8, 256):
            n = xkb.xkb_keymap_key_get_syms_by_level(km, kc, 0, level, ctypes.byref(syms))
            if any(syms[i] == sym for i in range(n)):
                return kc - 8, level == 1
    raise ValueError(f"no key produces {name!r}")

# --- minimal wayland wire protocol -------------------------------------------
def u32(v): return struct.pack("<I", v & 0xFFFFFFFF)
def fixed(v): return struct.pack("<i", int(v * 256))
def wstr(s):
    b = s.encode() + b"\0"
    return u32(len(b)) + b + b"\0" * (-len(b) % 4)

class Wayland:
    def __init__(self):
        path = os.path.join(os.environ["XDG_RUNTIME_DIR"], os.environ["WAYLAND_DISPLAY"])
        self.s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.s.connect(path)
        self.next_id, self.buf, self.globals, self.done = 2, b"", {}, set()

    def new_id(self):
        self.next_id += 1
        return self.next_id - 1

    def send(self, obj, op, payload=b"", fds=()):
        data = u32(obj) + u32((8 + len(payload)) << 16 | op) + payload
        if fds:
            socket.send_fds(self.s, [data], list(fds))
        else:
            self.s.sendall(data)

    def pump(self, block=True):
        if not block and not select.select([self.s], [], [], 0)[0]:
            return
        chunk = self.s.recv(65536)
        if not chunk:
            sys.exit("compositor went away")
        self.buf += chunk
        while len(self.buf) >= 8:
            obj, sz_op = struct.unpack_from("<II", self.buf)
            size, op = sz_op >> 16, sz_op & 0xFFFF
            if len(self.buf) < size:
                break
            body, self.buf = self.buf[8:size], self.buf[size:]
            if obj == 1 and op == 0:  # wl_display.error
                oid, code, ln = struct.unpack_from("<III", body)
                sys.exit(f"wayland error on object {oid} code {code}: {body[12:12+ln-1].decode()}")
            if obj == self.registry and op == 0:  # wl_registry.global
                name, ln = struct.unpack_from("<II", body)
                iface = body[8:8 + ln - 1].decode()
                ver = struct.unpack_from("<I", body, 8 + ln + (-ln % 4))[0]
                self.globals[iface] = (name, ver)
            elif op == 0 and obj in self.callbacks:  # wl_callback.done
                self.done.add(obj)

    def roundtrip(self):
        cb = self.new_id()
        self.callbacks.add(cb)
        self.send(1, 0, u32(cb))  # wl_display.sync
        while cb not in self.done:
            self.pump()

    def bind(self, iface, version):
        name, have = self.globals[iface]
        oid = self.new_id()
        self.send(self.registry, 0, u32(name) + wstr(iface) + u32(min(version, have)) + u32(oid))
        return oid

def now_ms():
    return int(time.monotonic() * 1000) & 0xFFFFFFFF

def serve():
    wl = Wayland()
    wl.callbacks = set()
    wl.registry = wl.new_id()
    wl.send(1, 1, u32(wl.registry))  # wl_display.get_registry
    wl.roundtrip()
    for need in ("wl_seat", "zwp_virtual_keyboard_manager_v1", "zwlr_virtual_pointer_manager_v1"):
        if need not in wl.globals:
            sys.exit(f"compositor lacks {need}")
    seat = wl.bind("wl_seat", 1)
    kbd_mgr = wl.bind("zwp_virtual_keyboard_manager_v1", 1)
    ptr_mgr = wl.bind("zwlr_virtual_pointer_manager_v1", 1)
    kbd, ptr = wl.new_id(), wl.new_id()
    wl.send(kbd_mgr, 0, u32(seat) + u32(kbd))  # create_virtual_keyboard
    wl.send(ptr_mgr, 0, u32(seat) + u32(ptr))  # create_virtual_pointer
    km, km_str = keymap()
    fd = os.memfd_create("keymap")
    os.write(fd, km_str + b"\0")
    wl.send(kbd, 0, u32(1) + u32(len(km_str) + 1), fds=[fd])  # keymap(xkb_v1, fd, size)
    os.close(fd)
    wl.roundtrip()

    def key(code, pressed): wl.send(kbd, 1, u32(now_ms()) + u32(code) + u32(1 if pressed else 0))
    def frame(): wl.send(ptr, 4)
    def button(code, pressed): wl.send(ptr, 2, u32(now_ms()) + u32(code) + u32(1 if pressed else 0)); frame()
    def move(x, y): wl.send(ptr, 1, u32(now_ms()) + u32(x) + u32(y) + u32(W) + u32(H)); frame()

    def tap(name, hold=0.05):
        code, shift = lookup(km, name)
        if shift: key(42, True)
        key(code, True); time.sleep(hold); key(code, False)
        if shift: key(42, False)

    def handle(args):
        cmd = args[0]
        if cmd == "key": tap(args[1], float(args[2]) if len(args) > 2 else 0.05)
        elif cmd in ("down", "up"): key(lookup(km, args[1])[0], cmd == "down")
        elif cmd == "type":
            for ch in " ".join(args[1:]): tap(ch); time.sleep(0.02)
        elif cmd == "move": move(int(args[1]), int(args[2]))
        elif cmd == "rel": wl.send(ptr, 0, u32(now_ms()) + fixed(float(args[1])) + fixed(float(args[2]))); frame()
        elif cmd == "click":
            rest = args[1:]
            if len(rest) >= 2: move(int(rest[0]), int(rest[1])); rest = rest[2:]; time.sleep(0.05)
            b = BTN[rest[0] if rest else "left"]
            button(b, True); time.sleep(0.05); button(b, False)
        elif cmd == "scroll":
            steps = int(args[1])
            wl.send(ptr, 5, u32(0))  # axis_source wheel
            wl.send(ptr, 7, u32(now_ms()) + u32(0) + fixed(15 * steps) + struct.pack("<i", steps))  # axis_discrete
            frame()
        else: raise ValueError(f"unknown command {cmd!r}")

    if os.path.exists(SOCK): os.unlink(SOCK)
    srv = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    srv.bind(SOCK); srv.listen(4)
    print(f"vinput ready on {SOCK}", flush=True)
    while True:
        r, _, _ = select.select([srv, wl.s], [], [])
        if wl.s in r: wl.pump()
        if srv in r:
            conn, _ = srv.accept()
            with conn:
                args = conn.recv(4096).decode().split("\0")
                try: handle(args); wl.roundtrip(); conn.sendall(b"ok")
                except Exception as e: conn.sendall(f"error: {e}".encode())

def client(args):
    c = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    try: c.connect(SOCK)
    except OSError: sys.exit(f"vinput daemon not running ({SOCK})")
    c.sendall("\0".join(args).encode()); c.shutdown(socket.SHUT_WR)
    reply = c.recv(4096).decode()
    if reply != "ok": sys.exit(reply)

if __name__ == "__main__":
    if len(sys.argv) < 2: sys.exit(__doc__)
    serve() if sys.argv[1] == "serve" else client(sys.argv[1:])
