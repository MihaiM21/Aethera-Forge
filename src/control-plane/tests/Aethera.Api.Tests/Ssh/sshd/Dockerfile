# Throw-away sshd for the SSH transport tests (WP2.3). Not for production use: the passwords are test values.
#
#   docker build -t aethera-test-sshd src/control-plane/tests/Aethera.Api.Tests/Ssh/sshd
#   docker run -d --name aethera-test-sshd -p 15250:22 aethera-test-sshd
#   export AETHERA_TEST_SSH=127.0.0.1:15250 AETHERA_TEST_SSH_CONTAINER=aethera-test-sshd
#
# The image has a real sshd, sudo, a POSIX shell, python3 (a tiny HTTP server for health probes), util-linux (runuser) and
# stand-ins for `docker` (records its argv and answers with canned JSON), `systemctl` and `aethera-agent`, so the tests exercise the real
# SSH.NET client, real shell quoting and the real install script without a Docker daemon or systemd.
FROM debian:bookworm-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends openssh-server sudo python3 util-linux passwd procps ca-certificates coreutils \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /run/sshd

RUN useradd -m -s /bin/bash deploy \
 && echo 'deploy:aethera-test' | chpasswd \
 && echo 'root:aethera-test' | chpasswd \
 && echo 'deploy ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/deploy \
 && useradd -m -s /bin/bash plain && echo 'plain:aethera-test' | chpasswd \
 && sed -i 's/^#\?PermitRootLogin.*/PermitRootLogin yes/; s/^#\?PasswordAuthentication.*/PasswordAuthentication yes/; s/^#\?KbdInteractiveAuthentication.*/KbdInteractiveAuthentication yes/' /etc/ssh/sshd_config

COPY docker-stub.py /usr/local/bin/docker
COPY systemctl-stub.sh /usr/local/bin/systemctl
RUN chmod 0755 /usr/local/bin/docker /usr/local/bin/systemctl

EXPOSE 22 8080
CMD ["/usr/sbin/sshd", "-D", "-e"]
