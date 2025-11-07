// Native.cs — interop + constants, with correct io_uring_params layout

using System.Runtime.InteropServices;

namespace Overdrive;

internal static unsafe class Native
{
    // ---- Tunables ----
    internal const int RING_ENTRIES = 8192;
    internal const int MAX_FD = 200_000;
    internal const int RECV_BUF_SZ = 8192;
    internal const int BR_ENTRIES = 4096;     // power-of-two
    internal const int BR_GID = 1;
    internal const int BACKLOG = 65535;
    internal const int BATCH_CQES = 512;

    // Fixed IP/Port
    internal const string LISTEN_IP = "0.0.0.0";
    internal const ushort LISTEN_PORT = 8080;

    // ---- liburing interop ----
    [StructLayout(LayoutKind.Sequential)] internal struct io_uring { public fixed ulong _[128]; }
    [StructLayout(LayoutKind.Sequential)] internal struct io_uring_sqe { public fixed ulong _[8]; }
    [StructLayout(LayoutKind.Sequential)] internal struct io_uring_cqe { public ulong user_data; public int res; public uint flags; }
    internal struct io_uring_buf_ring { } // opaque

    // Critical: must match linux/include/uapi/linux/io_uring.h
    [StructLayout(LayoutKind.Sequential)]
    internal struct io_uring_params
    {
        public uint sq_entries;
        public uint cq_entries;
        public uint flags;
        public uint sq_thread_cpu;
        public uint sq_thread_idle;
        public uint features;
        public uint wq_fd;
        public uint resv0;
        public uint resv1;
        public uint resv2;

        public sqring_offsets sq_off;
        public cqring_offsets cq_off;

        [StructLayout(LayoutKind.Sequential)]
        public struct sqring_offsets
        {
            public uint head;
            public uint tail;
            public uint ring_mask;
            public uint ring_entries;
            public uint flags;
            public uint dropped;
            public uint array;
            public uint resv1;
            public uint resv2;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct cqring_offsets
        {
            public uint head;
            public uint tail;
            public uint ring_mask;
            public uint ring_entries;
            public uint overflow;
            public uint cqes;
            public uint flags;
            public uint resv1;
            public uint resv2;
            public uint resv3;
        }
    }

    // ---- shim API (Cdecl) ----
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern int shim_queue_init_params(uint entries, io_uring* ring, io_uring_params* p);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern io_uring_sqe* shim_get_sqe(io_uring* ring);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern int shim_submit(io_uring* ring);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern int shim_wait_cqe(io_uring* ring, io_uring_cqe** cqe);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern int shim_peek_batch_cqe(io_uring* ring, io_uring_cqe** cqes, uint count);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern void shim_cqe_seen(io_uring* ring, io_uring_cqe* cqe);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern uint shim_sq_ready(io_uring* ring);

    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern void shim_prep_multishot_accept(io_uring_sqe* sqe, int lfd, int flags);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern void shim_prep_recv_multishot_select(io_uring_sqe* sqe, int fd, uint buf_group, int flags);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern void shim_prep_send(io_uring_sqe* sqe, int fd, void* buf, uint nbytes, int flags);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern void shim_prep_recv(io_uring_sqe* sqe, int fd, void* buf, uint nbytes, int flags);

    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern void shim_sqe_set_data64(io_uring_sqe* sqe, ulong data);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern ulong shim_cqe_get_data64(io_uring_cqe* cqe);

    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern io_uring_buf_ring* shim_setup_buf_ring(io_uring* ring, uint entries, uint bgid, uint flags, out int ret);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern void shim_free_buf_ring(io_uring* ring, io_uring_buf_ring* br, uint entries, uint bgid);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern void shim_buf_ring_add(io_uring_buf_ring* br, void* addr, uint len, ushort bid, ushort mask, uint idx);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern void shim_buf_ring_advance(io_uring_buf_ring* br, uint count);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern int shim_cqe_has_buffer(io_uring_cqe* cqe);
    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern uint shim_cqe_buffer_id(io_uring_cqe* cqe);

    [DllImport("uringshim", CallingConvention = CallingConvention.Cdecl)] internal static extern int shim_ring_fd(io_uring* ring);

    // ---- libc ----
    [DllImport("libc")] internal static extern int socket(int domain, int type, int proto);
    [DllImport("libc")] internal static extern int setsockopt(int fd, int level, int optname, void* optval, uint optlen);
    [DllImport("libc")] internal static extern int bind(int fd, sockaddr_in* addr, uint len);
    [DllImport("libc")] internal static extern int listen(int fd, int backlog);
    [DllImport("libc")] internal static extern int fcntl(int fd, int cmd, int arg);
    [DllImport("libc")] internal static extern int close(int fd);
    [DllImport("libc")] internal static extern int inet_pton(int af, sbyte* src, void* dst);

    internal const int AF_INET = 2, SOCK_STREAM = 1, SOL_SOCKET = 1, SO_REUSEADDR = 2, SO_REUSEPORT = 15;
    internal const int IPPROTO_TCP = 6, TCP_NODELAY = 1;
    internal const int F_GETFL = 3, F_SETFL = 4, O_NONBLOCK = 0x800, SOCK_NONBLOCK = 0x800;

    [StructLayout(LayoutKind.Sequential)] internal struct in_addr { public uint s_addr; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct sockaddr_in
    {
        public ushort sin_family;
        public ushort sin_port;
        public in_addr sin_addr;
        public fixed byte sin_zero[8];
    }
    internal static ushort Htons(ushort x) => (ushort)((x << 8) | (x >> 8));

    internal enum UdKind : uint { Accept = 1, Recv = 2, Send = 3 }
    internal static ulong PackUd(UdKind k, int fd) => ((ulong)k << 32) | (uint)fd;
    internal static UdKind UdKindOf(ulong ud) => (UdKind)(ud >> 32);
    internal static int UdFdOf(ulong ud) => (int)(ud & 0xffffffff);
}
