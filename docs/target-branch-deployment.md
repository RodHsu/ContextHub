# Target-branch deployment

A protected branch under targets/ identifies an environment. The deployment controller
polls that branch and reads promotion.json from each new commit on its first-parent
history. Only the target branch is fetched on unchanged polls. A new promotion
requires a fresh fetch of main for source authorization. The first run uses the head
of the target branch. Keep one complete promotion
record per commit and protect the branch from force-pushes and deletion.

The record contains target, sourceSha, version, and rollbackContract. Use a 40-character
source SHA already reachable from the current origin/main. The contract identifies a
deployment whose schema, data, and runtime settings are compatible with its rollback
image. Review the contract before promoting a new release. The runner checks the new
source against the configured migration baseline and rejects SQL migration changes.
The source tree on the target branch has no role in the build.

## Configure the controller

Install Invoke-TargetDeployment.ps1 and TargetDeployment.Transaction.ps1 together in a
directory writable only by the deployment account and trusted administrators. Keep the
configuration, deployment journal, and retained image tags under the same ownership.
Use PowerShell 7.4 or later, Git, Docker, and Docker Compose with support for up --wait.
Use a scheduler that ignores overlapping runs and has a time limit longer than the
combined build, apply, and rollback budgets.

The runner JSON lists repository, targetBranch, stateDirectory, schemaBaselineSha,
rollbackContract, buildSeconds, applySeconds, rollbackSeconds, and groups. A group has
a Compose project name, an absolute project directory, ordered Compose files, an
optional envFile, and a list of services. Each service supplies its Compose name and
a Dockerfile path relative to the approved source root. Select existing single-replica
services with health checks. Include the actual ordered Compose configuration for
each group and give the scheduler the same Docker context and account as the
validated setup.

Run the controller with Invoke-TargetDeployment.ps1 -ConfigPath <path>. Its output has
the target revision, status, and stage. Persist this limited status output for
operations. Keep host configuration, health credentials, and detailed runtime logs
in authorized storage.

## Schema changes

Before recording a promotion, compare the proposed source with the deployment
controller's configured schema baseline.

```powershell
git diff --name-status $SchemaBaselineSha $SourceSha -- src/Memory.Infrastructure/Sql/Migrations
```

If this lists changes, keep the promotion off the automated target branch while
the schema rollout is reviewed. Capture the migration ledger before and after
the upgrade, and verify that the retained rollback application remains
compatible with the resulting schema. The controller can accept the source
after the deployment owner configures a baseline with the approved migration
set and confirms the rollback contract.

## Failure handling

The controller first builds locally tagged images and verifies the current running
image identities and health. It saves a recovery journal before applying changes. It
starts only the listed Compose services. If apply fails, health is missing, or the
deadline expires, the controller stops the previous Compose client and reapplies
the retained images using a separate deadline. It verifies the restored image IDs
and container health. After a settled run it keeps the retained rollback image tags
and generated overrides still referenced by containers or configured Compose inputs.
An interrupted transaction is recovered before the next
promotion is considered. A rollback failure preserves the journal and latches the
controller. Inspect the live images and the journal before operator recovery.

Use an isolated project to exercise process errors and timeouts. Verify production
Compose inputs, a compatible source and schema, and the deployment account before
enabling production. The automated rollback restores application images. It cannot
reverse PostgreSQL migrations or undo data writes.
