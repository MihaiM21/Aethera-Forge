package backup

import (
	"bytes"
	"crypto/hkdf"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"os"
	"testing"
	"testing/iotest"
)

func testKey(b byte) []byte { return bytes.Repeat([]byte{b}, KeySize) }

func pattern(n int) []byte {
	p := make([]byte, n)
	for i := range p {
		p[i] = byte(i % 251)
	}
	return p
}

func seal(t *testing.T, key []byte, plain []byte) []byte {
	t.Helper()
	var buf bytes.Buffer
	w, err := NewWriter(&buf, key, "k1")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := w.Write(plain); err != nil {
		t.Fatal(err)
	}
	if err := w.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func open(obj, key []byte) ([]byte, error) {
	r, err := NewReader(bytes.NewReader(obj), key)
	if err != nil {
		return nil, err
	}
	return io.ReadAll(r)
}

func TestRoundTripSizes(t *testing.T) {
	for _, n := range []int{0, 1, 100, ChunkSize - 1, ChunkSize, ChunkSize + 1, 2 * ChunkSize, 2*ChunkSize + 7, 5*ChunkSize + 123} {
		plain := pattern(n)
		obj := seal(t, testKey(1), plain)
		got, err := open(obj, testKey(1))
		if err != nil {
			t.Fatalf("n=%d: %v", n, err)
		}
		if !bytes.Equal(got, plain) {
			t.Fatalf("n=%d: round trip differs", n)
		}
		// The canonical writer never emits an empty last chunk, except for an empty plaintext.
		chunks := (n + ChunkSize - 1) / ChunkSize
		if n == 0 {
			chunks = 1
		}
		want := 5 + 2 + SaltSize + n + chunks*tagSize
		if len(obj) != want {
			t.Fatalf("n=%d: object length %d, want %d", n, len(obj), want)
		}
	}
}

func TestReaderReadsInSmallPiecesAndSlowSources(t *testing.T) {
	plain := pattern(3*ChunkSize + 11)
	obj := seal(t, testKey(1), plain)
	r, err := NewReader(iotest.OneByteReader(bytes.NewReader(obj)), testKey(1))
	if err != nil {
		t.Fatal(err)
	}
	if err := iotest.TestReader(r, plain); err != nil {
		t.Fatal(err)
	}
}

func TestWriterAcceptsOddWrites(t *testing.T) {
	plain := pattern(2*ChunkSize + 500)
	var buf bytes.Buffer
	w, _ := NewWriter(&buf, testKey(1), "k1")
	for off := 0; off < len(plain); {
		n := min(7919, len(plain)-off)
		if _, err := w.Write(plain[off : off+n]); err != nil {
			t.Fatal(err)
		}
		off += n
	}
	if err := w.Close(); err != nil {
		t.Fatal(err)
	}
	got, err := open(buf.Bytes(), testKey(1))
	if err != nil || !bytes.Equal(got, plain) {
		t.Fatalf("odd writes: %v", err)
	}
}

func TestReaderToleratesEmptyLastChunk(t *testing.T) {
	// Another implementation may end an exact multiple of the chunk size with an empty final chunk.
	salt := bytes.Repeat([]byte{9}, SaltSize)
	hdr, _ := header("k1", salt)
	aead, _ := deriveAEAD(testKey(1), salt)
	plain := pattern(ChunkSize)
	n0, n1 := chunkNonce(0, false), chunkNonce(1, true)
	obj := append([]byte{}, hdr...)
	obj = aead.Seal(obj, n0[:], plain, hdr)
	obj = aead.Seal(obj, n1[:], nil, hdr)
	got, err := open(obj, testKey(1))
	if err != nil || !bytes.Equal(got, plain) {
		t.Fatalf("empty last chunk: %v", err)
	}
}

func TestTruncationFails(t *testing.T) {
	plain := pattern(3*ChunkSize + 5)
	obj := seal(t, testKey(1), plain)
	cuts := []int{
		len(obj) - 1,                        // inside the tag of the last chunk
		len(obj) - (5 + tagSize),            // the last chunk loses its payload
		5 + 2 + SaltSize + ChunkSize + 16,   // exactly after the first chunk
		5 + 2 + SaltSize + 2*(ChunkSize+16), // exactly after the second chunk
		5 + 2 + SaltSize,                    // header only
	}
	for _, c := range cuts {
		got, err := open(obj[:c], testKey(1))
		if !errors.Is(err, ErrAuth) {
			t.Fatalf("cut at %d: err = %v (read %d bytes)", c, err, len(got))
		}
	}
	// A header cut short is a format error.
	if _, err := open(obj[:10], testKey(1)); !errors.Is(err, ErrFormat) {
		t.Fatalf("short header: %v", err)
	}
}

func TestTruncationAtChunkBoundaryOfExactMultiple(t *testing.T) {
	obj := seal(t, testKey(1), pattern(2*ChunkSize))
	cut := 5 + 2 + SaltSize + ChunkSize + tagSize
	if _, err := open(obj[:cut], testKey(1)); !errors.Is(err, ErrAuth) {
		t.Fatalf("err = %v", err)
	}
}

func TestFlippedBitFails(t *testing.T) {
	plain := pattern(2*ChunkSize + 100)
	obj := seal(t, testKey(1), plain)
	hdrLen := 5 + 2 + SaltSize
	for _, pos := range []int{hdrLen, hdrLen + ChunkSize + 20, len(obj) - 1, 6, hdrLen - 1} {
		c := append([]byte{}, obj...)
		c[pos] ^= 0x01
		if _, err := open(c, testKey(1)); err == nil {
			t.Fatalf("flipped bit at %d went unnoticed", pos)
		}
	}
}

func TestReorderedChunksFail(t *testing.T) {
	obj := seal(t, testKey(1), pattern(3*ChunkSize))
	hdrLen := 5 + 2 + SaltSize
	cs := ChunkSize + tagSize
	c := append([]byte{}, obj[:hdrLen]...)
	c = append(c, obj[hdrLen+cs:hdrLen+2*cs]...)
	c = append(c, obj[hdrLen:hdrLen+cs]...)
	c = append(c, obj[hdrLen+2*cs:]...)
	if _, err := open(c, testKey(1)); !errors.Is(err, ErrAuth) {
		t.Fatalf("reordered: %v", err)
	}
}

func TestWrongKeyFails(t *testing.T) {
	obj := seal(t, testKey(1), pattern(1000))
	if _, err := open(obj, testKey(2)); !errors.Is(err, ErrAuth) {
		t.Fatalf("wrong key: %v", err)
	}
}

func TestBadMagicAndKeyHandling(t *testing.T) {
	if _, err := NewReader(bytes.NewReader([]byte("NOPE.........")), testKey(1)); !errors.Is(err, ErrFormat) {
		t.Fatalf("magic: %v", err)
	}
	if _, err := ParseKey("short"); err == nil {
		t.Fatal("short key accepted")
	}
	if _, err := ParseKey(base64.StdEncoding.EncodeToString(make([]byte, 16))); err == nil {
		t.Fatal("16-byte key accepted")
	}
	if _, err := NewWriter(io.Discard, testKey(1), ""); err == nil {
		t.Fatal("empty key id accepted")
	}
	// Errors never carry the key.
	obj := seal(t, testKey(1), pattern(10))
	_, err := open(obj, testKey(7))
	key := base64.StdEncoding.EncodeToString(testKey(7))
	if err == nil || bytes.Contains([]byte(err.Error()), []byte(key)) {
		t.Fatalf("error leaks key: %v", err)
	}
}

func TestReaderKeyID(t *testing.T) {
	obj := seal(t, testKey(1), pattern(10))
	r, _ := NewReader(bytes.NewReader(obj), testKey(1))
	if r.KeyID() != "k1" {
		t.Fatalf("key id %q", r.KeyID())
	}
}

// ---------------------------------------------------------------------------
// Test vector shared with the .NET implementation.

type vectorEntry struct {
	Name            string `json:"name"`
	PlaintextLength int    `json:"plaintext_length"`
	PlaintextSHA256 string `json:"plaintext_sha256"`
	ObjectLength    int    `json:"object_length"`
	ObjectSHA256    string `json:"object_sha256"`
	ObjectBase64    string `json:"object_base64,omitempty"`
}

type vectorFile struct {
	Description string `json:"description"`
	Format      string `json:"format"`
	// Inputs.
	KeyBase64   string `json:"key_base64"`
	KeyID       string `json:"key_id"`
	SaltHex     string `json:"salt_hex"`
	HKDFInfo    string `json:"hkdf_info"`
	ChunkSize   int    `json:"chunk_size"`
	Plaintext   string `json:"plaintext"`
	DerivedKey  string `json:"derived_key_hex"`
	HeaderHex   string `json:"header_hex"`
	Nonces      string `json:"nonces"`
	ChunkLayout string `json:"chunk_layout"`
	// The main vector (plaintext of 150000 bytes).
	PlaintextLength int    `json:"plaintext_length"`
	PlaintextSHA256 string `json:"plaintext_sha256"`
	ObjectLength    int    `json:"object_length"`
	ObjectSHA256    string `json:"object_sha256"`
	ObjectBase64    string `json:"object_base64"`
	// Edge cases (same key id, key and salt; objects without the base64 body).
	Additional []vectorEntry `json:"additional_vectors"`
}

func sum(b []byte) string { h := sha256.Sum256(b); return hex.EncodeToString(h[:]) }

func vectorObject(t *testing.T, plain []byte) []byte {
	t.Helper()
	var buf bytes.Buffer
	w, err := NewWriterSalt(&buf, testKey(0x01), "test", bytes.Repeat([]byte{0x02}, SaltSize))
	if err != nil {
		t.Fatal(err)
	}
	w.Write(plain)
	if err := w.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func buildVector(t *testing.T) vectorFile {
	salt := bytes.Repeat([]byte{0x02}, SaltSize)
	hdr, _ := header("test", salt)
	okm, _ := hkdfKeyForTest(testKey(0x01), salt)
	plain := pattern(150000)
	obj := vectorObject(t, plain)
	v := vectorFile{
		Description: "AEB1 test vector (ADR 0010). Encrypting plaintext with key, key_id and salt must produce exactly object_base64; " +
			"decrypting it must produce the plaintext. Regenerated and compared by the Go test TestVectorFile.",
		Format:     "AEB1",
		KeyBase64:  base64.StdEncoding.EncodeToString(testKey(0x01)),
		KeyID:      "test",
		SaltHex:    hex.EncodeToString(salt),
		HKDFInfo:   HKDFInfo,
		ChunkSize:  ChunkSize,
		Plaintext:  "byte i = i mod 251, for i in 0..plaintext_length-1",
		DerivedKey: hex.EncodeToString(okm),
		HeaderHex:  hex.EncodeToString(hdr),
		Nonces:     "12 bytes per chunk: 11-byte big-endian chunk counter (starting at 0) followed by 0x00, or 0x01 on the last chunk",
		ChunkLayout: "header | chunk 0 | chunk 1 | ...; every chunk is AES-256-GCM(derived key, nonce, plaintext chunk, aad = header) = ciphertext | 16-byte tag; " +
			"plaintext chunks are 65536 bytes except the last, which holds the remaining 1..65536 bytes (0 only for an empty plaintext). " +
			"A reader treats a chunk as the last one when nothing follows it and must accept an additional empty last chunk after a full one.",
		PlaintextLength: len(plain),
		PlaintextSHA256: sum(plain),
		ObjectLength:    len(obj),
		ObjectSHA256:    sum(obj),
		ObjectBase64:    base64.StdEncoding.EncodeToString(obj),
	}
	for _, c := range []struct {
		name string
		n    int
	}{{"empty", 0}, {"one_byte", 1}, {"one_full_chunk", ChunkSize}, {"two_full_chunks", 2 * ChunkSize}, {"chunk_plus_one", ChunkSize + 1}} {
		p := pattern(c.n)
		o := vectorObject(t, p)
		v.Additional = append(v.Additional, vectorEntry{
			Name: c.name, PlaintextLength: len(p), PlaintextSHA256: sum(p), ObjectLength: len(o), ObjectSHA256: sum(o),
		})
	}
	return v
}

func TestVectorFile(t *testing.T) {
	want := buildVector(t)
	data, err := json.MarshalIndent(want, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	data = append(data, '\n')
	const path = "testdata/aeb1-vector.json"
	if os.Getenv("AEB1_UPDATE_VECTOR") == "1" {
		if err := os.MkdirAll("testdata", 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, data, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	got, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("%v (regenerate with AEB1_UPDATE_VECTOR=1)", err)
	}
	if !bytes.Equal(bytes.ReplaceAll(got, []byte("\r\n"), []byte("\n")), data) {
		t.Fatal("testdata/aeb1-vector.json is stale (regenerate with AEB1_UPDATE_VECTOR=1 go test ./internal/backup -run TestVectorFile)")
	}

	// And the committed file decrypts to the documented plaintext.
	var v vectorFile
	if err := json.Unmarshal(got, &v); err != nil {
		t.Fatal(err)
	}
	key, err := ParseKey(v.KeyBase64)
	if err != nil {
		t.Fatal(err)
	}
	obj, _ := base64.StdEncoding.DecodeString(v.ObjectBase64)
	if sum(obj) != v.ObjectSHA256 || len(obj) != v.ObjectLength {
		t.Fatal("object checksum/length mismatch in the vector")
	}
	plain, err := open(obj, key)
	if err != nil || sum(plain) != v.PlaintextSHA256 || len(plain) != v.PlaintextLength || !bytes.Equal(plain, pattern(150000)) {
		t.Fatalf("vector does not decrypt to the documented plaintext: %v", err)
	}
	if v.ObjectLength != 41+65552*2+18928+16 {
		t.Fatalf("unexpected vector object length %d", v.ObjectLength)
	}
}

func hkdfKeyForTest(key, salt []byte) ([]byte, error) {
	return hkdf.Key(sha256.New, key, salt, HKDFInfo, KeySize)
}
