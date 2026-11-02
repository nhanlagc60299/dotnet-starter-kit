# GitHub Actions runner with its own podman engine (podman-in-podman). Jobs get a Docker API that
# only reaches this engine, never the host's podman (and through it the host's files).
FROM ghcr.io/actions/actions-runner:2.338.2

USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends podman crun fuse-overlayfs uidmap iptables netavark aardvark-dns \
 && rm -rf /var/lib/apt/lists/*

COPY containers.conf /etc/containers/containers.conf
COPY crun-ci /usr/local/bin/crun-ci
COPY entrypoint.sh /usr/local/bin/ci-runner-entrypoint
ENTRYPOINT ["/usr/local/bin/ci-runner-entrypoint"]
CMD ["./run.sh"]
