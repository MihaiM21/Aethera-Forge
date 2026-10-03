package docker

import (
	"encoding/json"
	"strings"
)

// StripInspectEnv removes environment VALUES from raw `docker inspect` JSON
// (Config.Env entries become "NAME="), because env values are where secrets
// live. Names are kept so the UI can still show which variables exist.
func StripInspectEnv(raw []byte) ([]byte, error) {
	var doc map[string]any
	if err := json.Unmarshal(raw, &doc); err != nil {
		return nil, err
	}
	if cfg, ok := doc["Config"].(map[string]any); ok {
		if env, ok := cfg["Env"].([]any); ok {
			for i, e := range env {
				s, _ := e.(string)
				if idx := strings.IndexByte(s, '='); idx >= 0 {
					env[i] = s[:idx+1]
				}
			}
		}
	}
	return json.Marshal(doc)
}
