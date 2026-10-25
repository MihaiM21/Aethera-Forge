package compose

import (
	"fmt"
	"regexp"
	"strings"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

var envNameRe = regexp.MustCompile(`^[A-Za-z_][A-Za-z0-9_]*$`)

// dotenvValue renders a value for a compose `--env-file` so that it reads back exactly: single quotes are literal (no
// `$` interpolation, no ` #` comment, no escapes) and may span lines; a value that contains a single quote cannot use them
// and is written in double quotes with `\`, `"` and `$` escaped and line breaks as \n and \r.
//
// The interpolation file only feeds `${VAR}` in the compose files. Getting the variables into the containers is the control
// plane's business: it adds `environment:` entries to the override file.
func dotenvValue(v string) string {
	if !strings.Contains(v, "'") {
		return "'" + v + "'"
	}
	r := strings.NewReplacer(`\`, `\\`, `"`, `\"`, `$`, `\$`, "\n", `\n`, "\r", `\r`)
	return `"` + r.Replace(v) + `"`
}

// envValue is the value of an EnvVar, plain or secret.
func envValue(v *agentv1.EnvVar) string {
	if s := v.GetSecret(); s != nil {
		return s.GetValue()
	}
	return v.GetPlain()
}

// dotenvFile renders the interpolation variables; names are validated, values cannot contain NUL.
func dotenvFile(vars []*agentv1.EnvVar) (string, error) {
	var b strings.Builder
	for _, v := range vars {
		if !envNameRe.MatchString(v.GetName()) {
			return "", bad("invalid environment variable name %q", v.GetName())
		}
		val := envValue(v)
		if strings.ContainsRune(val, 0) {
			return "", bad("environment variable %s contains a NUL byte", v.GetName())
		}
		fmt.Fprintf(&b, "%s=%s\n", v.GetName(), dotenvValue(val))
	}
	return b.String(), nil
}

// buildArgs validates the build arguments and returns the process environment entries (NAME=value) and the names for
// `--build-arg NAME`: values never appear in an argument vector. A name that is also an interpolation variable must have
// the same value, because compose reads both from one environment.
func buildArgs(args, env []*agentv1.EnvVar) (pairs, names []string, err error) {
	vars := map[string]string{}
	for _, v := range env {
		vars[v.GetName()] = envValue(v)
	}
	seen := map[string]bool{}
	for _, a := range args {
		n, val := a.GetName(), envValue(a)
		if !envNameRe.MatchString(n) {
			return nil, nil, bad("invalid build argument name %q", n)
		}
		if strings.ContainsRune(val, 0) {
			return nil, nil, bad("build argument %s contains a NUL byte", n)
		}
		if other, ok := vars[n]; ok && other != val {
			return nil, nil, bad("%s is both an environment variable and a build argument with different values", n)
		}
		if seen[n] {
			continue
		}
		seen[n] = true
		pairs = append(pairs, n+"="+val)
		names = append(names, n)
	}
	return pairs, names, nil
}
