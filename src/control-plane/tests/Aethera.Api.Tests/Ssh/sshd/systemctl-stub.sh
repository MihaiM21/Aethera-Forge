#!/bin/sh
# Test stand-in for systemctl: records the call, succeeds.
printf '%s\n' "$*" >> /var/log/systemctl-calls.log
exit 0
