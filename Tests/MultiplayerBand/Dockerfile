FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    xvfb xauth python3 libgl1 libglx-mesa0 libgl1-mesa-dri libegl1 libvulkan1 mesa-vulkan-drivers \
    libasound2t64 libpulse0 libxcursor1 libxrandr2 libxi6 libxinerama1 libxss1 libgtk-3-0t64 libudev1 libhidapi-hidraw0 \
    ca-certificates && rm -rf /var/lib/apt/lists/*
RUN printf 'pcm.!default { type null }\nctl.!default { type null }\n' > /etc/asound.conf
