// build:
//   gcc -O2 -fPIC -shared uringshim.c -o liburingshim.so -luring -ldl
//
// Notes:
// - Opaque handle exported to C# (IntPtr) so .NET never passes its own io_uring struct.
// - SQPOLL honored when the environment has SQPOLL=1 (sq_thread_idle is passed in).
// - Buf-ring helpers are resolved at runtime via dlsym; if unavailable -> EOPNOTSUPP.
// - Compatible with older liburing (no io_uring_sqe_set_buf_group/io_uring_cqe_has_buf).

#define _GNU_SOURCE
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <dlfcn.h>
#include <liburing.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===== Compatibility fallbacks (older liburing) ========================== */

#ifndef IOSQE_BUFFER_SELECT
# define IOSQE_BUFFER_SELECT (1U << 5)
#endif

#ifndef IORING_CQE_F_BUFFER
# define IORING_CQE_F_BUFFER  (1U << 0)
#endif
#ifndef IORING_CQE_BUFFER_SHIFT
# define IORING_CQE_BUFFER_SHIFT 16
#endif
#ifndef IORING_CQE_BUFFER_MASK
# define IORING_CQE_BUFFER_MASK  0xFFFF
#endif

/* ===== Opaque ring handle =============================================== */

struct RingHandle {
    struct io_uring ring;
};

/* ===== Optional (runtime) buf-ring APIs via dlsym ======================= */

typedef struct io_uring_buf_ring* (*pfn_setup_buf_ring)(struct io_uring*, unsigned, unsigned, unsigned, int*);
typedef void (*pfn_free_buf_ring)(struct io_uring*, struct io_uring_buf_ring*, unsigned, unsigned);
typedef void (*pfn_buf_ring_add)(struct io_uring_buf_ring*, void*, unsigned, unsigned short, unsigned short, unsigned);
typedef void (*pfn_buf_ring_advance)(struct io_uring_buf_ring*, unsigned);

static pfn_setup_buf_ring   p_io_uring_setup_buf_ring   = NULL;
static pfn_free_buf_ring    p_io_uring_free_buf_ring    = NULL;
static pfn_buf_ring_add     p_io_uring_buf_ring_add     = NULL;
static pfn_buf_ring_advance p_io_uring_buf_ring_advance = NULL;

void shim_prep_recv(struct io_uring_sqe* sqe, int fd, void* buf, unsigned len, int flags)
{
    io_uring_prep_recv(sqe, fd, buf, len, flags);
}

static void resolve_bufring_symbols_once(void)
{
    static int done = 0;
    if (done) return;
    done = 1;

    // RTLD_DEFAULT searches the global symbol table (works even without explicit dlopen)
    p_io_uring_setup_buf_ring   = (pfn_setup_buf_ring)   dlsym(RTLD_DEFAULT, "io_uring_setup_buf_ring");
    p_io_uring_free_buf_ring    = (pfn_free_buf_ring)    dlsym(RTLD_DEFAULT, "io_uring_free_buf_ring");
    p_io_uring_buf_ring_add     = (pfn_buf_ring_add)     dlsym(RTLD_DEFAULT, "io_uring_buf_ring_add");
    p_io_uring_buf_ring_advance = (pfn_buf_ring_advance) dlsym(RTLD_DEFAULT, "io_uring_buf_ring_advance");
}

/* ===== Basic ring lifecycle ============================================ */

struct RingHandle* shim_ring_create(unsigned entries, unsigned sqpoll_idle_ms, int* out_errno)
{
    struct io_uring_params p = {0};

    // Respect SQPOLL=1 env var (cap_sys_admin typically required)
    const char* env = getenv("SQPOLL");
    if (env && env[0] == '1' && env[1] == '\0') {
        p.flags = IORING_SETUP_SQPOLL;
        p.sq_thread_idle = (sqpoll_idle_ms ? sqpoll_idle_ms : 2000);
    }

    struct RingHandle* h = (struct RingHandle*)calloc(1, sizeof(*h));
    if (!h) { if (out_errno) *out_errno = -ENOMEM; return NULL; }

    int ret = io_uring_queue_init_params(entries, &h->ring, &p);
    if (ret < 0) {
        if (out_errno) *out_errno = ret;
        free(h);
        return NULL;
    }

    if (out_errno) *out_errno = 0;
    return h;
}

void shim_ring_destroy(struct RingHandle* h)
{
    if (!h) return;
    io_uring_queue_exit(&h->ring);
    free(h);
}

int shim_ring_fd(struct RingHandle* h)
{
    if (!h) return -1;
    return h->ring.ring_fd;
}

/* ===== SQE/CQE helpers (handle-based) ================================== */

struct io_uring_sqe* shim_get_sqe_h(struct RingHandle* h)
{
    return io_uring_get_sqe(&h->ring);
}

int shim_submit_h(struct RingHandle* h)
{
    return io_uring_submit(&h->ring);
}

int shim_wait_cqe_h(struct RingHandle* h, struct io_uring_cqe** cqe)
{
    return io_uring_wait_cqe(&h->ring, cqe);
}

int shim_peek_batch_cqe_h(struct RingHandle* h, struct io_uring_cqe** cqes, unsigned count)
{
    return io_uring_peek_batch_cqe(&h->ring, cqes, count);
}

void shim_cqe_seen_h(struct RingHandle* h, struct io_uring_cqe* cqe)
{
    io_uring_cqe_seen(&h->ring, cqe);
}

unsigned shim_sq_ready_h(struct RingHandle* h)
{
    return io_uring_sq_ready(&h->ring);
}

/* ===== Prep helpers that operate directly on SQE ======================== */

void shim_prep_multishot_accept(struct io_uring_sqe* sqe, int lfd, int flags)
{
    io_uring_prep_multishot_accept(sqe, lfd, NULL, NULL, flags);
}

void shim_prep_recv_multishot_select(struct io_uring_sqe* sqe, int fd, unsigned buf_group, int flags)
{
    // Multishot recv, with buffer-selection from group bgid
    io_uring_prep_recv_multishot(sqe, fd, NULL, 0, flags);
    // Older liburing may lack io_uring_sqe_set_buf_group(); set fields directly:
    sqe->buf_group = (unsigned short)buf_group;
    sqe->flags    |= IOSQE_BUFFER_SELECT;
}

void shim_prep_send(struct io_uring_sqe* sqe, int fd, void* buf, unsigned nbytes, int flags)
{
    io_uring_prep_send(sqe, fd, buf, nbytes, flags);
}

void shim_sqe_set_data64(struct io_uring_sqe* sqe, unsigned long long data)
{
    io_uring_sqe_set_data64(sqe, data);
}

unsigned long long shim_cqe_get_data64(struct io_uring_cqe* cqe)
{
    return io_uring_cqe_get_data64(cqe);
}

/* ===== Buf-ring wrappers (handle-based, runtime-resolved) =============== */

// Returns pointer to buf-ring or NULL; *out_ret is 0 on success or -errno style on failure.
struct io_uring_buf_ring* shim_setup_buf_ring_h(struct RingHandle* h, unsigned entries, unsigned bgid, unsigned flags, int* out_ret)
{
    resolve_bufring_symbols_once();

    if (!p_io_uring_setup_buf_ring) {
        if (out_ret) *out_ret = -EOPNOTSUPP;
        return NULL;
    }

    int ret = 0;
    struct io_uring_buf_ring* br = p_io_uring_setup_buf_ring(&h->ring, entries, bgid, flags, &ret);
    if (out_ret) *out_ret = ret;
    return br;
}

void shim_free_buf_ring_h(struct RingHandle* h, struct io_uring_buf_ring* br, unsigned entries, unsigned bgid)
{
    resolve_bufring_symbols_once();

    if (!p_io_uring_free_buf_ring) {
        // No-op if not available
        return;
    }
    p_io_uring_free_buf_ring(&h->ring, br, entries, bgid);
}

void shim_buf_ring_add(struct io_uring_buf_ring* br, void* addr, unsigned len, unsigned short bid, unsigned short mask, unsigned idx)
{
    resolve_bufring_symbols_once();

    if (p_io_uring_buf_ring_add) {
        p_io_uring_buf_ring_add(br, addr, len, bid, mask, idx);
        return;
    }
    // Without add(): we can’t safely poke internals -> silently ignore (or you may set *out_ret upstream).
    // Caller already feature-gates using shim_setup_buf_ring_h’s -EOPNOTSUPP.
}

void shim_buf_ring_advance(struct io_uring_buf_ring* br, unsigned count)
{
    resolve_bufring_symbols_once();

    if (p_io_uring_buf_ring_advance) {
        p_io_uring_buf_ring_advance(br, count);
        return;
    }
    // See comment above.
}

// CQE buffer helpers (older liburing compatibility)
int shim_cqe_has_buffer(struct io_uring_cqe* cqe)
{
    return (cqe->flags & IORING_CQE_F_BUFFER) ? 1 : 0;
}

unsigned shim_cqe_buffer_id(struct io_uring_cqe* cqe)
{
    return (cqe->flags >> IORING_CQE_BUFFER_SHIFT) & IORING_CQE_BUFFER_MASK;
}

#ifdef __cplusplus
}
#endif
