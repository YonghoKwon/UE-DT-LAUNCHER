# Authority store platform checks

Run these checks from the repository root on Windows with Docker Desktop in Linux mode. The harness is test-only. Use a new private evidence directory for each run.

```powershell
$evidence = 'C:\path\to\new-evidence'
dotnet publish tools/AuthorityHarness/AuthorityHarness.csproj -c Release -r win-x64 --self-contained true -o "$evidence\win-x64"
dotnet publish tools/AuthorityHarness/AuthorityHarness.csproj -c Release -r linux-x64 --self-contained true -o "$evidence\linux-x64"
& "$evidence\win-x64\UeDtLauncher.AuthorityHarness.exe" platform-suite "$evidence\ntfs"
python tools/AuthorityHarness/run-linux-platform.py --publish "$evidence\linux-x64" --output "$evidence\linux"
```

Use a local NTFS path for the Windows fixture. The Linux runner pins each Docker command to the local Docker Desktop Linux named pipe and verifies the staged harness DLL against both containers. It uses the 01D Rocky 8.4 amd64 digest and the 8.4 BaseOS vault. Each service has a private named volume, no published ports, and no network during testing. Only XFS is privileged, with a private sparse disk. The runner records mounts, package versions, process and container restarts, and before/after Docker and loop-device inventories. It preserves the run's resources if it cannot verify XFS detachment. After verified cleanup it removes the run's Compose resources and image tag. The shared build cache is retained.

G2 keeps the original HEAD and Catalog handles open in four processes. The readers check both handles and read a current store snapshot repeatedly until generation 500. Each reader must see at least one intermediate generation. The JSON output counts repeated valid HEAD reads where the store's seam observes retries. A failure after the store's retry budget fails G2. G3 holds the old Catalog file without delete sharing to exercise Windows deferred collection and Linux unlink. The next commit after releasing that handle uses the existing store instance. G4 aborts only lease holders started by the harness. G5 exercises seven 01S1 commit boundaries and checks exact areas, event identities, the derived JSONL, and the following commit in a fresh process.

For G9 after 01S6, use fresh paths and add `final` to the Windows command and `--final` to the Linux command. These switches run G2 and one process crash at each seam per filesystem. Process and container interruption here exercise restart behavior. Company host and power-loss evidence require separate runs.

For the initialization mount regressions, use a disposable Linux container with a writable private volume at `/dt/store`, a read-only parent and the published Linux server. In two fresh containers mount a second private volume at `/dt/store/authority` and `/dt/store/.authority-init.v1`, respectively. Run `bash check-initialization-mount.sh /path/to/UeDtLauncher.DistributionServer authority` for the first mount and change the last argument to `.authority-init.v1` for the second. The script requires different mount IDs, the published CLI diagnostic and identical inventory before and after refusal. Named volumes on the same backing filesystem can have the same device number. [Mount regression](check-initialization-mount.sh)
