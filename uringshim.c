// liburingshim.c
#define _GNU_SOURCE
#include <liburing.h>
#include <stdint.h>

/* -------- Core ring ops -------- */
int  shim_queue_init_params(unsigned entries, struct io_uring* ring, struct io_uring_params* p) {
    return io_uring_queue_init_params(entries, ring, p);
}
int  shim_submit(struct io_uring* ring) { return io_uring_submit(ring); }
int  shim_wait_cqe(struct io_uring* ring, struct io_uring_cqe** cqe) { return io_uring_wait_cqe(ring, cqe); }
int  shim_peek_batch_cqe(struct io_uring* ring, struct io_uring_cqe** cqes, unsigned count) {
    return io_uring_peek_batch_cqe(ring, cqes, count);
}
void shim_cqe_seen(struct io_uring* ring, struct io_uring_cqe* cqe) { io_uring_cqe_seen(ring, cqe); }
unsigned shim_sq_ready(struct io_uring* ring) { return io_uring_sq_ready(ring); }
struct io_uring_sqe* shim_get_sqe(struct io_uring* ring) { return io_uring_get_sqe(ring); }

/* Handy for debugging: fetch ring fd (check /proc/<pid>/fdinfo/<fd>) */
int shim_ring_fd(struct io_uring* ring) { return ring->ring_fd; }

/* -------- Multishot ops -------- */
void shim_prep_multishot_accept(struct io_uring_sqe* sqe, int lfd, int flags) {
    io_uring_prep_multishot_accept(sqe, lfd, NULL, NULL, flags);
}
void shim_prep_recv_multishot_select(struct io_uring_sqe* sqe, int fd, unsigned buf_group, int flags) {
    io_uring_prep_recv_multishot(sqe, fd, NULL, 0, flags);
    sqe->flags |= IOSQE_BUFFER_SELECT;
    sqe->buf_group = buf_group;
}

/* -------- Basic recv & send -------- */
void shim_prep_send(struct io_uring_sqe* sqe, int fd, const void* buf, unsigned nbytes, int flags) {
    io_uring_prep_send(sqe, fd, buf, nbytes, flags);
}
void shim_prep_recv(struct io_uring_sqe* sqe, int fd, void* buf, unsigned nbytes, int flags) {
    io_uring_prep_recv(sqe, fd, buf, nbytes, flags);
}

/* -------- User-data helpers -------- */
void shim_sqe_set_data64(struct io_uring_sqe* sqe, unsigned long long data) {
    io_uring_sqe_set_data64(sqe, data);
}
unsigned long long shim_cqe_get_data64(const struct io_uring_cqe* cqe) {
    return io_uring_cqe_get_data64(cqe);
}

/* -------- Buf-ring helpers -------- */
struct io_uring_buf_ring* shim_setup_buf_ring(struct io_uring* ring,
                                              unsigned entries,
                                              unsigned bgid,
                                              unsigned flags,
                                              int* ret_out)
{
    return io_uring_setup_buf_ring(ring, entries, (int)bgid, flags, ret_out);
}

void shim_free_buf_ring(struct io_uring* ring, struct io_uring_buf_ring* br, unsigned entries, unsigned bgid) {
    io_uring_free_buf_ring(ring, br, entries, (int)bgid);
}

void shim_buf_ring_add(struct io_uring_buf_ring* br, void* addr, unsigned len,
                       unsigned short bid, unsigned short mask, unsigned idx)
{
    io_uring_buf_ring_add(br, addr, len, bid, mask, idx);
}

void shim_buf_ring_advance(struct io_uring_buf_ring* br, unsigned count) {
    io_uring_buf_ring_advance(br, count);
}

/* -------- CQE buffer helpers -------- */
int shim_cqe_has_buffer(const struct io_uring_cqe* cqe) {
    return (cqe->flags & IORING_CQE_F_BUFFER) != 0;
}
unsigned shim_cqe_buffer_id(const struct io_uring_cqe* cqe) {
    return cqe->flags >> IORING_CQE_BUFFER_SHIFT;
}
