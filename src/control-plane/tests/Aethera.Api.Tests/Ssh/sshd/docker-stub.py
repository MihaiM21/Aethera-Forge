#!/usr/bin/env python3
"""Test stand-in for the docker CLI (WP2.3 SSH transport tests).

Records every call (argv, the content of --env-file, whether stdin carried a password) as one JSON line in /tmp/docker-calls.jsonl and
answers the read-only commands the SSH transport uses with canned output. It never runs anything, so the tests can check that hostile
strings arrive as single, unmodified arguments after crossing a real SSH exec and a real shell.
"""
import json, os, stat, sys, time

args = sys.argv[1:]
record = {"argv": args, "ts": time.time()}

# Content and permissions of an env-file at call time (the transport removes it afterwards).
if "--env-file" in args:
    path = args[args.index("--env-file") + 1]
    try:
        record["envFile"] = open(path).read()
        record["envFileMode"] = oct(stat.S_IMODE(os.stat(path).st_mode))
    except OSError as e:
        record["envFileError"] = str(e)

if "--password-stdin" in args:
    record["stdin"] = sys.stdin.read()
    record["dockerConfig"] = os.environ.get("DOCKER_CONFIG", "")

if os.environ.get("AETHERA_STUB_ENV"):
    record["env"] = {k: v for k, v in os.environ.items() if k.startswith("BUILD_") or k == "TOKEN"}

with open("/tmp/docker-calls.jsonl", "a") as log:
    log.write(json.dumps(record) + "\n")

CONTAINER = {
    "Id": "a" * 64, "Name": "/web", "Created": "2026-10-01T10:00:00.123456789Z", "Image": "sha256:" + "b" * 64, "RestartCount": 0,
    "State": {"Status": "running", "Running": True, "ExitCode": 0, "OOMKilled": False, "StartedAt": "2026-10-01T10:00:01.5Z", "FinishedAt": "0001-01-01T00:00:00Z",
              "Health": {"Status": "healthy"}},
    "Config": {"Image": "nginx:1.27", "Labels": {"aethera.application.id": "00000000-0000-0000-0000-000000000001"}, "Env": ["SECRET=hunter2"]},
    "NetworkSettings": {"Ports": {"80/tcp": [{"HostIp": "0.0.0.0", "HostPort": "8080"}]},
                        "Networks": {"bridge": {"IPAddress": "172.17.0.2", "MacAddress": "02:42:ac:11:00:02", "Aliases": None}}},
    "Mounts": [{"Type": "volume", "Name": "data", "Destination": "/data", "RW": True}],
}

cmd = " ".join(a for a in args if not a.startswith("-"))
if args[:2] == ["container", "inspect"] and "--format" in args:
    print("healthy")
elif args[:2] == ["container", "inspect"]:
    print(json.dumps([CONTAINER]))
elif args[:2] == ["container", "ls"] and "--quiet" in args:
    print("a" * 64)
elif args[:2] == ["image", "inspect"]:
    print(json.dumps([{"Id": "sha256:" + "c" * 64, "RepoTags": ["nginx:1.27"], "RepoDigests": ["nginx@sha256:" + "d" * 64], "Size": 187000000,
                       "Created": "2026-09-01T00:00:00Z", "Architecture": "amd64", "Os": "linux", "Config": {"Labels": {}}}]))
elif args[:2] == ["image", "pull"]:
    print("1.27: Pulling from library/nginx")
    print("Status: Image is up to date for nginx:1.27")
elif args[:1] == ["version"]:
    print("27.3.1")
elif args[:1] == ["info"]:
    print(json.dumps({"ServerVersion": "27.3.1", "Driver": "overlay2", "CgroupVersion": "2", "DockerRootDir": "/var/lib/docker",
                      "ContainersRunning": 1, "ContainersStopped": 0, "Images": 1, "Swarm": {"LocalNodeState": "inactive"}, "SecurityOptions": ["name=seccomp"]}))
elif args[:1] == ["stats"]:
    print(json.dumps({"ID": "a" * 64, "Name": "web", "CPUPerc": "1.50%", "MemUsage": "20MiB / 1GiB", "NetIO": "1.2kB / 3kB", "BlockIO": "0B / 0B", "PIDs": "4"}))
elif args[:1] == ["ps"]:
    print(json.dumps({"ID": "a" * 64, "Names": "web", "State": "running", "Labels": "aethera.application.id=00000000-0000-0000-0000-000000000001"}))
elif args[:1] == ["compose"] and "ps" in args:
    print(json.dumps({"ID": "e" * 12, "Name": "demo-web-1", "Service": "web", "Image": "nginx:1.27", "State": "running", "Health": "", "ExitCode": 0,
                      "Publishers": [{"URL": "0.0.0.0", "TargetPort": 80, "PublishedPort": 8081, "Protocol": "tcp"}]}))
elif args[:1] == ["compose"]:
    print("compose ok")
elif args[:2] == ["container", "create"]:
    print("a" * 64)
sys.exit(0)
