# What Campus's encryption does, and what it does not

This document exists because the honest version is more useful than a reassuring one.

## What is protected

Everything in the workspace is encrypted at rest with AES-256-GCM under a key that only exists in
memory while the vault is unlocked.

**Files.** Stored content-addressed and chunked. The bytes on disk are ciphertext; so are the
file names, which are keyed hashes rather than the SHA-256 of the content — the plaintext hash
never touches the file system, so nobody can confirm they have a copy of a particular file by
looking at a directory listing.

**The database.** SQLCipher, keyed from the same master key. Titles, tags, due dates, teacher
names, note bodies and the change journal are all inside it. A locked workspace's database file
does not even begin with SQLite's own header.

**Search.** The full-text index lives inside that database, so search terms and the text they
match are encrypted with everything else.

**Thumbnails and extracted text.** Same store, same keys. A generated preview of a page is as
protected as the page.

Verified by tests in `tests/Campus.Core.Tests` that write real content and then assert it does
not appear anywhere in the bytes on disk.

## The key hierarchy

```
Windows Hello  ─┐
                ├─→  Master Key  ─→  content · names · metadata · database · index · thumbnails · sync
Recovery Key   ─┘
```

Both protectors wrap the same master key; neither is derived from the other.

**Windows Hello** is not a check that can be skipped. The key-encryption key is derived from a
signature that only the Hello-protected private key can produce, and that key lives in the TPM
where the machine has one. A protector that merely asked Hello "was that the user?" and then
handed over the key would be bypassable by anyone willing to call the unwrap themselves; this one
is not, because without a successful face, fingerprint or PIN there is no signature, and without
the signature there is no key.

**The recovery key** is 120 bits of randomness, shown once at creation and never stored — not
even encrypted. It is the only way back in if Windows Hello is re-enrolled, the PC is replaced or
the TPM is cleared.

Locking zeroes every key in memory and closes the database in the same operation, so there is no
state where the files are locked but the database is still readable.

## What is *not* protected

**Anyone with your unlocked session.** While Campus is open and unlocked, the keys are in memory
and the content is on screen. That is the point of it being open.

**An administrator on this machine.** Someone with administrator rights can attach a debugger to
the Campus process, read its memory, and take the master key out of it. They can also screenshot
what is displayed, log keystrokes, or replace Campus with a version that keeps a copy. No
application running as a normal user process can prevent this — not Campus, not any password
manager, not any disk encryption product.

**Malware running as you.** Same reasoning. Code running under your account can do what you can
do.

**Traffic analysis of the vault directory.** File sizes and modification times are visible.
Someone watching the directory can tell that you imported a 40 MB file this afternoon, though not
what it was.

**Anything you export.** Export writes plaintext to wherever you asked for it. That copy is an
ordinary file with ordinary permissions.

## What the "cannot be opened from Explorer" requirement really buys

The original ask was for files that cannot be opened outside Campus and cannot be copied except
from within it. Hiding a folder or tightening its ACL does not achieve that: the owner of the
account, and any administrator, can take ownership back.

Encryption does achieve the part that matters. A file in the vault is not merely inconvenient to
open from Explorer — it is meaningless without the key. Copying it gets you ciphertext. Opening
it in another application shows nothing. That is a real guarantee, and it holds even if the drive
is removed and read on another machine.

What it cannot do is stop *you*, while you are logged in and unlocked, from exporting a file and
then doing whatever you like with it. Campus makes that a deliberate action rather than an
accident, and Sensitive Mode can require re-authentication for it, but it cannot make it
impossible.

## Auto-lock

Auto-lock measures idleness from the last real interaction rather than firing on a fixed
interval, so it never lands mid-sentence. The policies are 5, 10 and 30 minutes, plus locking
when Windows locks and when the app closes.

## If you lose the recovery key and Windows Hello stops working

The workspace is unrecoverable. This is not a limitation to be worked around later — a vault with
a back door is a vault with a back door. Keep the recovery key somewhere that is not this
computer.
