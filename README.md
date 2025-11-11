[![NuGet](https://img.shields.io/nuget/v/Overdrive.svg)](https://www.nuget.org/packages/Overdrive/)


# Overdrive 🔥 — A NativeAOT C# HTTP/1.1 Server on io_uring

Overdrive is a high-performance HTTP/1.1 server written in C# and compiled with **.NET NativeAOT**, driving the Linux kernel’s **io_uring** interface directly via a thin C shim. It focuses on:

- Zero-GC hot path (unmanaged slabs, pooled objects)
- io_uring multishot operations (`accept`, `recv`) + **buf-ring** (provided buffers)
- Minimal syscalls, batching, and backpressure
- Performance predictable at high concurrency

If you like servers fast, tight, and close to the kernel, you’re in the right place.

---

## Features

- ⚙️ **io_uring-native**: multishot accept + recv with buffer selection
- 🧠 **NativeAOT compiled**: zero JIT, tiny memory footprint
- 🔄 **Per-core worker model** with lock-free FD dispatch
- 🧱 **Unmanaged slabs + buf-ring** for zero-allocation receive path
- 🚀 **TFB-ready**: optimized for high RPS and stable latency

---

## Requirements

| Requirement | Version |
|------------|---------|
| Linux Kernel | **6.0+ recommended** |
| liburing | Recent (2.x or newer recommended) |
| .NET SDK | **9.0+** (NativeAOT) |
| Compiler Tools | `clang`, `build-essential` |

---

## Build & Run

### 1) Clone

```bash
git clone https://github.com/<yourname>/Overdrive.git
cd Overdrive
```

## Install dependencies 

```bash
sudo apt-get update
sudo apt-get install -y clang build-essential liburing-dev linux-headers-$(uname -r)
```

## Build shim and publish nativeAOT

```bash
cd native
clang -O2 -fPIC -shared uringshim.c -o liburingshim.so -luring -ldl
cd ..

cd Overdrive
dotnet publish -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishAot=true \
  -p:OptimizationPreference=Speed \
  -o out

cp ../native/liburingshim.so ./out/
```