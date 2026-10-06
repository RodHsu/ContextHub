# Embedding tokenizer contract

The default `sentencepiece-raw-v1` profile retains the existing runtime behavior.
`sentencepiece-vocab-id-v2` is an explicit candidate contract; selecting it requires
a pinned asset bundle SHA-256. Changing this contract requires a new vector
generation and reindex before any retrieval cutover.

The candidate resolves token pieces to the IDs declared in `tokenizer.json`,
retains BOS/EOS on truncation, uses padding ID 1 with attention mask 0, and applies
the declared character map, ASCII space normalization, Metaspace and literal
special-token rules. Unsupported tokenizer structures fail initialization.
Blank embedding requests remain invalid.

The asset fingerprint covers each profile asset's relative name, byte length and
SHA-256 in a sorted `e5-asset-bundle-v1` JSON envelope. Startup verifies the bundle
before loading it. Release assets must remain immutable after verification.
Candidate model keys include the asset digest, vector dimensions, maximum token
length and tokenizer contract. Cache and vector identities cannot reuse a legacy
model key. Embedding HTTP responses must match the configured key, dimensions,
token limit and finite vector shape before entering caches or storage.

Reindex requests may omit the model key or assert the configured provider's exact
key. They cannot rename its output into another generation. This also applies to
previously queued jobs: a mismatched key fails before inference or vector writes.
Select a matching provider before enqueueing work for a different model. The
previous behavior that accepted arbitrary vector labels is no longer supported.
Project-wide and single-item jobs retain their tenant and owner predicates.

Microsoft.ML.Tokenizers 2.0.0 exposes its pinned character-map normalization through
the encoding API. The candidate uses the complete normalized text returned with
an ID limit of one, then applies the declared space policy and encodes pieces.
This avoids dependency on private library APIs; it performs an extra segmentation
pass, whose cost must be measured with the intended workload before cutover.

Rollout preparation must verify token IDs against the pinned original tokenizer,
actual model inference, batch/padding behavior, retrieval quality, complete chunk
coverage, generation isolation, capacity and rollback. A bounded synthetic shadow
run does not establish representative Production quality or release acceptance.
Keep the legacy generation intact during shadow evaluation; activate the candidate
only through a separately authorized coordinated release and coverage gate.
