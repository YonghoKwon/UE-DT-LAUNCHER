# PROD-06B local enrollment evidence

The historical five-PC Rocky 8.4 run used Linux and Windows binaries published
from `1687616006cb0ec2570ab9ea3d8167b9e30f6013`. Both original publisher
records report that `sourceHead` and exit 0. It is the first parent of
`0b34e419ae1195a817b5550fe54adaf30bed5c35`, the merge of PR #39.
This follow-up updates the reproduction script on the PR #39 base.
It did not run Docker or publish a new cohort. [Publication and gate evidence](evidence.json)
· [PR #39](https://github.com/YonghoKwon/UE-DT-LAUNCHER/pull/39)

| Gate | Verdict | Historical observation |
| --- | --- | --- |
| G1 | N/A | No repository edits for the historical run. Linux and Windows publication exit 0. |
| G2 | PASS | Public enrollment bundles only. Export audit found zero private-key, credential-file or secret-header matches. |
| G3 | PASS | `client-enroll --dry-run` planned four keys and three policy additions. Key list, policy and store tree hashes, audit count and authority generation matched. |
| G4 | PASS | Five peers on separate fixed IPs produced HTTP 200, 200, 200, 403 and 401. The three verified signed Catalogs contained one, one and zero releases. |
| G5 | PASS | Original scope. Policy mode and owner stayed `root:uedt-distribution 640`. The serve identity could read the policy. Zero temporary policy files remained. |
| G6 | PASS | sim-a and sim-b each received Manifest HTTP 200 and matched two installed files against the Manifest by size and SHA-256. |
| G7 | PASS | Invalid address CSV exit 1. Key state, policy, audit and store tree matched. |
| G8 | PASS | Same batch added zero keys and zero audit entries. |
| G9 | PASS | Revoked key returned 401, replacement key returned 200, revoked key remained at 401. |
| G10 | NOT_RUN | The product has no command to edit an existing client's allowed address. A supplemental move of the same client and key yielded 403 and then 200 after restoration. |
| G11 | PASS | Zero owned Docker resources remained and the resource inventory showed zero differences. BuildKit cache was retained under the test contract. |

Extended ACL equality is **unverified in the historical PROD-06B run**.
PR #34 separately verified that the published server preserved an ACL during
`client-enroll` on Rocky 8.4 after the LinuxFileAccess fix. The updated
[reproduction](README.md) seeds a named ACL and requires equal numeric
`getfacl` output around policy replacement. The updated cleanup records its
verdict and exits nonzero on G11 FAIL. This evidence covers only the local
synthetic fixture. [PR #34 Rocky 8.4 verification](https://github.com/YonghoKwon/UE-DT-LAUNCHER/pull/34)
