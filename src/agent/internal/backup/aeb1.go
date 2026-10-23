// Package backup implements the agent side of ADR 0010: the AEB1 stream
// encryption, the storage locations (S3-compatible stores and the local
// backup directory) and the backup, restore and delete operations built on
// them. Everything streams: a dump is encrypted while it is produced and
// uploaded while it is encrypted, so no step stages the whole stream on disk.
package backup

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/hkdf"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
)

// Constants of the AEB1 format (ADR 0010 "Format AEB1").
const (
	// Magic starts every object.
	Magic = "AEB1"
	// SaltSize is the length of the random per-object salt.
	SaltSize = 32
	// KeySize is the length of the stream key.
	KeySize = 32
	// ChunkSize is the plaintext size of every chunk but the last.
	ChunkSize = 65536
	// HKDFInfo is the HKDF info string of the per-object key derivation.
	HKDFInfo = "aethera/backup/aeb1"

	tagSize   = 16
	nonceSize = 12
)

// Errors of the format. They never contain key material.
var (
	// ErrFormat means the object is not an AEB1 object (bad magic, header or framing).
	ErrFormat = errors.New("not a valid AEB1 backup object")
	// ErrAuth means the authenticated decryption failed: wrong key, corrupted or truncated object.
	ErrAuth = errors.New("backup object failed authentication (wrong key, corrupted or truncated)")
)

// ParseKey decodes the base64 stream key (32 raw bytes).
func ParseKey(b64 string) ([]byte, error) {
	raw, err := base64.StdEncoding.DecodeString(b64)
	if err != nil || len(raw) != KeySize {
		return nil, errors.New("backup key must be 32 bytes, base64 encoded")
	}
	return raw, nil
}

// header builds "AEB1" | len(key_id) | key_id | salt.
func header(keyID string, salt []byte) ([]byte, error) {
	if len(keyID) == 0 || len(keyID) > 255 {
		return nil, errors.New("backup key id must be 1 to 255 bytes")
	}
	for _, r := range keyID {
		if r < 0x20 || r > 0x7e {
			return nil, errors.New("backup key id must be printable ASCII")
		}
	}
	if len(salt) != SaltSize {
		return nil, errors.New("salt must be 32 bytes")
	}
	h := make([]byte, 0, len(Magic)+1+len(keyID)+SaltSize)
	h = append(h, Magic...)
	h = append(h, byte(len(keyID)))
	h = append(h, keyID...)
	h = append(h, salt...)
	return h, nil
}

// deriveAEAD derives the per-object AES-256-GCM from the stream key and salt.
func deriveAEAD(key, salt []byte) (cipher.AEAD, error) {
	if len(key) != KeySize {
		return nil, errors.New("backup key must be 32 bytes")
	}
	okm, err := hkdf.Key(sha256.New, key, salt, HKDFInfo, KeySize)
	if err != nil {
		return nil, err
	}
	block, err := aes.NewCipher(okm)
	if err != nil {
		return nil, err
	}
	return cipher.NewGCM(block)
}

// chunkNonce is the 11-byte big-endian chunk counter followed by the final flag.
func chunkNonce(counter uint64, final bool) [nonceSize]byte {
	var n [nonceSize]byte
	binary.BigEndian.PutUint64(n[3:11], counter)
	if final {
		n[11] = 1
	}
	return n
}

// Writer encrypts everything written to it into the underlying writer.
// Close must be called to write the final chunk; an object without it fails
// to decrypt (that is the point).
type Writer struct {
	w       io.Writer
	aead    cipher.AEAD
	aad     []byte
	buf     []byte // pending plaintext, at most ChunkSize
	out     []byte
	counter uint64
	started bool // header written
	closed  bool
	err     error
}

// NewWriter starts an AEB1 object with a random salt.
func NewWriter(w io.Writer, key []byte, keyID string) (*Writer, error) {
	salt := make([]byte, SaltSize)
	if _, err := rand.Read(salt); err != nil {
		return nil, err
	}
	return NewWriterSalt(w, key, keyID, salt)
}

// NewWriterSalt is NewWriter with an explicit salt (test vectors only: a
// salt must never be reused with the same key).
func NewWriterSalt(w io.Writer, key []byte, keyID string, salt []byte) (*Writer, error) {
	h, err := header(keyID, salt)
	if err != nil {
		return nil, err
	}
	aead, err := deriveAEAD(key, salt)
	if err != nil {
		return nil, err
	}
	return &Writer{w: w, aead: aead, aad: h, buf: make([]byte, 0, ChunkSize), out: make([]byte, 0, ChunkSize+tagSize)}, nil
}

func (e *Writer) writeHeader() error {
	if e.started {
		return nil
	}
	e.started = true
	_, err := e.w.Write(e.aad)
	return err
}

// flush seals the pending plaintext as one chunk.
func (e *Writer) flush(final bool) error {
	if err := e.writeHeader(); err != nil {
		return err
	}
	n := chunkNonce(e.counter, final)
	e.counter++
	e.out = e.aead.Seal(e.out[:0], n[:], e.buf, e.aad)
	e.buf = e.buf[:0]
	_, err := e.w.Write(e.out)
	return err
}

// Write buffers p and seals a full chunk once more data follows it, so the
// last chunk is only known at Close (it is full-size when the plaintext is a
// multiple of the chunk size, and empty only for an empty plaintext).
func (e *Writer) Write(p []byte) (int, error) {
	if e.closed {
		return 0, errors.New("backup: write after close")
	}
	if e.err != nil {
		return 0, e.err
	}
	total := 0
	for len(p) > 0 {
		if len(e.buf) == ChunkSize {
			if err := e.flush(false); err != nil {
				e.err = err
				return total, err
			}
		}
		n := min(ChunkSize-len(e.buf), len(p))
		e.buf = append(e.buf, p[:n]...)
		p = p[n:]
		total += n
	}
	return total, nil
}

// Close writes the final chunk. It does not close the underlying writer.
func (e *Writer) Close() error {
	if e.closed {
		return e.err
	}
	e.closed = true
	if e.err != nil {
		return e.err
	}
	e.err = e.flush(true)
	return e.err
}

// Reader decrypts an AEB1 object.
type Reader struct {
	r       io.Reader
	aead    cipher.AEAD
	aad     []byte
	keyID   string
	counter uint64
	enc     []byte // one encrypted chunk (read-ahead holds the next byte)
	plain   []byte // decrypted bytes not yet returned
	peek    []byte // bytes already read from r beyond the current chunk
	done    bool
	err     error
}

// NewReader reads and checks the header of an AEB1 object.
func NewReader(r io.Reader, key []byte) (*Reader, error) {
	var fixed [5]byte
	if _, err := io.ReadFull(r, fixed[:]); err != nil {
		return nil, fmt.Errorf("%w: header", ErrFormat)
	}
	if string(fixed[:4]) != Magic {
		return nil, ErrFormat
	}
	idLen := int(fixed[4])
	if idLen == 0 {
		return nil, fmt.Errorf("%w: empty key id", ErrFormat)
	}
	rest := make([]byte, idLen+SaltSize)
	if _, err := io.ReadFull(r, rest); err != nil {
		return nil, fmt.Errorf("%w: header", ErrFormat)
	}
	aad := append(append([]byte{}, fixed[:]...), rest...)
	aead, err := deriveAEAD(key, rest[idLen:])
	if err != nil {
		return nil, err
	}
	return &Reader{r: r, aead: aead, aad: aad, keyID: string(rest[:idLen]), enc: make([]byte, ChunkSize+tagSize)}, nil
}

// KeyID is the key id recorded in the header.
func (d *Reader) KeyID() string { return d.keyID }

// readFull fills p from the read-ahead bytes and r; it returns the bytes read
// (less than len(p) only at the end of the object).
func (d *Reader) readFull(p []byte) (int, error) {
	n := 0
	if len(d.peek) > 0 {
		n = copy(p, d.peek)
		d.peek = d.peek[n:]
	}
	if n < len(p) {
		m, err := io.ReadFull(d.r, p[n:])
		n += m
		if err == io.EOF || errors.Is(err, io.ErrUnexpectedEOF) {
			return n, nil
		}
		return n, err
	}
	return n, nil
}

// next decrypts the next chunk into d.plain.
func (d *Reader) next() error {
	n, err := d.readFull(d.enc)
	if err != nil {
		return err
	}
	final := false
	if n < len(d.enc) {
		final = true // short chunk: the end of the object
	} else {
		// A full-size chunk is the last one only when nothing follows it.
		var b [1]byte
		m, err := d.readFull(b[:])
		if err != nil {
			return err
		}
		if m == 0 {
			final = true
		} else {
			d.peek = append([]byte{b[0]}, d.peek...)
		}
	}
	if n < tagSize {
		return ErrAuth // truncated before the final chunk (or no chunk at all)
	}
	nonce := chunkNonce(d.counter, final)
	plain, err := d.aead.Open(d.plain[:0], nonce[:], d.enc[:n], d.aad)
	if err != nil {
		return ErrAuth
	}
	d.counter++
	d.plain = plain
	d.done = final
	return nil
}

// Read returns decrypted bytes. A truncated, reordered or modified object
// fails with ErrAuth no later than the point where the damage is found; the
// final chunk is only trusted once its tag has been checked.
func (d *Reader) Read(p []byte) (int, error) {
	for len(d.plain) == 0 {
		if d.err != nil {
			return 0, d.err
		}
		if d.done {
			return 0, io.EOF
		}
		if err := d.next(); err != nil {
			d.err = err
			return 0, err
		}
	}
	n := copy(p, d.plain)
	d.plain = d.plain[n:]
	return n, nil
}

// Verify decrypts everything in r and discards it, returning the plaintext
// length. It is how a restore proves an object intact before touching a database.
func Verify(r io.Reader, key []byte) (int64, error) {
	dr, err := NewReader(r, key)
	if err != nil {
		return 0, err
	}
	return io.Copy(io.Discard, dr)
}
