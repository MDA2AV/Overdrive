#define _GNU_SOURCE
#include <stdlib.h>
#include <string.h>
#include <liburing.h>

// gcc -O2 -fPIC -shared -o liburingshim.so uringshim.c -luring
// Thin, stable C ABI for P/Invoke from C#.

struct io_uring* shim_create_ring(unsigned entries, int* err_out)
{
    struct io_uring* ring = (struct io_uring*)malloc(sizeof(struct io_uring));
    if (!ring) { if (err_out) *err_out = -12; return NULL; } // -ENOMEM
    memset(ring, 0, sizeof(*ring));
    int rc = io_uring_queue_init(entries, ring, 0);
    if (rc < 0) { free(ring); if (err_out) *err_out = rc; return NULL; }
    if (err_out) *err_out = 0;
    return ring;
}

void shim_destroy_ring(struct io_uring* ring)
{
    if (!ring) return;
    io_uring_queue_exit(ring);
    free(ring);
}

/* Core ops */
int shim_submit(struct io_uring* ring) { return io_uring_submit(ring); }
int shim_wait_cqe(struct io_uring* ring, struct io_uring_cqe** cqe) { return io_uring_wait_cqe(ring, cqe); }
int shim_peek_batch_cqe(struct io_uring* ring, struct io_uring_cqe** cqes, unsigned count)
{
    return io_uring_peek_batch_cqe(ring, cqes, count);
}
void shim_cqe_seen(struct io_uring* ring, struct io_uring_cqe* cqe) { io_uring_cqe_seen(ring, cqe); }
unsigned shim_sq_ready(struct io_uring* ring) { return io_uring_sq_ready(ring); }
struct io_uring_sqe* shim_get_sqe(struct io_uring* ring) { return io_uring_get_sqe(ring); }

/* Multishot */
void shim_prep_multishot_accept(struct io_uring_sqe* sqe, int lfd, int flags)
{
    io_uring_prep_multishot_accept(sqe, lfd, NULL, NULL, flags);
}

void shim_prep_recv_multishot_select(struct io_uring_sqe* sqe, int fd, unsigned buf_group, int flags)
{
    io_uring_prep_recv_multishot(sqe, fd, NULL, 0, flags);
    sqe->flags |= IOSQE_BUFFER_SELECT;
    sqe->buf_group = buf_group;
}

/* User data */
void shim_sqe_set_data64(struct io_uring_sqe* sqe, unsigned long long data)
{
    io_uring_sqe_set_data64(sqe, data);
}
unsigned long long shim_cqe_get_data64(const struct io_uring_cqe* cqe)
{
    return io_uring_cqe_get_data64(cqe);
}

/* Buf-ring */
struct io_uring_buf_ring* shim_setup_buf_ring(struct io_uring* ring,
                                              unsigned entries,
                                              unsigned bgid,
                                              unsigned flags,
                                              int* ret_out)
{
    return io_uring_setup_buf_ring(ring, entries, (int)bgid, flags, ret_out);
}

void shim_free_buf_ring(struct io_uring* ring,
                        struct io_uring_buf_ring* br,
                        unsigned entries,
                        unsigned bgid)
{
    io_uring_free_buf_ring(ring, br, entries, (int)bgid);
}

void shim_buf_ring_add(struct io_uring_buf_ring* br,
                       void* addr,
                       unsigned len,
                       unsigned short bid,
                       unsigned short mask,
                       unsigned idx)
{
    io_uring_buf_ring_add(br, addr, len, bid, mask, idx);
}

void shim_buf_ring_advance(struct io_uring_buf_ring* br, unsigned count)
{
    io_uring_buf_ring_advance(br, count);
}

/* CQE buffer helpers */
int shim_cqe_has_buffer(const struct io_uring_cqe* cqe)
{
    return (cqe->flags & IORING_CQE_F_BUFFER) != 0;
}

unsigned shim_cqe_buffer_id(const struct io_uring_cqe* cqe)
{
    return cqe->flags >> IORING_CQE_BUFFER_SHIFT;
}

/* Send */
void shim_prep_send(struct io_uring_sqe* sqe,
                    int fd,
                    const void* buf,
                    unsigned nbytes,
                    int flags)
{
    io_uring_prep_send(sqe, fd, buf, nbytes, flags);
}
