# ---- Build (musl) --------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
# toolchain + liburing headers + numa for musl
RUN apk add --no-cache clang build-base zlib-dev linux-headers liburing-dev numactl-dev
WORKDIR /src
# copy native shim and your app
# adjust paths if yours differ
COPY uringshim.c ./native/
COPY /Overdrive/ ./Overdrive/
# build liburingshim.so against musl's liburing
WORKDIR /src/native
RUN clang -O2 -fPIC -shared uringshim.c -o liburingshim.so -luring -ldl
# publish your NativeAOT app for linux-musl-x64
WORKDIR /src/Overdrive
RUN dotnet publish -c Release \
    -r linux-musl-x64 \
    --self-contained true \
    -p:PublishAot=true \
    -p:OptimizationPreference=Speed \
    -p:GarbageCollectionAdaptationMode=0 \
    -o /app/out
# drop the shim next to the binary
RUN cp /src/native/liburingshim.so /app/out/
# ---- Runtime (musl) ------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-alpine
# runtime liburing and numa (needed unless you statically link them into the shim)
RUN apk add --no-cache liburing numactl
ENV URLS=http://+:8080 \
    LD_LIBRARY_PATH=/app
WORKDIR /app
COPY --from=build /app/out ./
# If your binary is named 'Overdrive'
RUN chmod +x ./Overdrive
EXPOSE 8080
ENTRYPOINT ["./Overdrive"]
