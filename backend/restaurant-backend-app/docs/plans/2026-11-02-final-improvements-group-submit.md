# Plan: Final improvements before merge (owner session id and group submit)

Status: Done

Follow-up to `2026-11-02-owner-session-id-and-group-submit.md` (Done; review verdict "approve with comments", 206 tests passing).

## Goal
Address finding 2 only: return the correct existing business exception when the GroupReservation status condition fails in the transaction. Validate with the full build, tests and a review of the complete Git diff.

## Guiding principle (highest priority: stability of the existing project)
- No unnecessary refactoring, architectural changes, optimizations or new abstractions.
- Only fix confirmed issues that could cause incorrect application behavior; everything else is reported, not changed.
- Preserve all existing functionality and business logic. No changes to the database schema, entities, AWS infrastructure or unrelated code.
- If a fix is risky or would need an architectural change, it is NOT implemented; the limitation is reported instead.
- Every agent first inspects the current code and confirms the "Proposed minimal changes" below. If its inspection contradicts them, or any change beyond them seems needed, it stops and reports instead of widening the scope.

## Proposed minimal changes (from inspection of the current code)
- Finding 1 (classes in combined files): explicitly skipped. Do not move classes or change any file structure.
- Finding 2 (confirmed incorrect behavior): `OrderRepository.SaveOrderAndSubmitGroupAsync` currently maps transaction cancellation to `GroupReservationSubmittedException` too broadly. Minimal fix: remove that repository catch/mapping; keep the transaction unchanged. In `GroupReservationService.SubmitGroupPreOrderAsync`, catch `TransactionCanceledException` only when `CancellationReasons` contains index 1 with `Code == "ConditionalCheckFailed"` (the GroupReservation write). For that case only, reload the group through existing `IGroupReservationRepository.GetGroupReservationByIdAsync` and throw the existing exception for its current state. If cancellation reasons are absent, index 1 is absent, or its code is anything else, rethrow the original transaction exception unchanged. No new exception types, interfaces, services or infrastructure.
- Finding 3 (concurrent order update): explicitly remains a documented limitation only. Do not change the order write, add concurrency protection, or modify the `Order` entity/schema. The order write remains unconditional; a concurrent edit could be overwritten. The existing group `Open` condition still prevents duplicate group submissions.

## Requirements
### Transaction failure handling (confirmed issue, minimal fix)
- `OrderRepository.SaveOrderAndSubmitGroupAsync` must no longer map transaction cancellation to a business exception; remove only that catch, leaving transaction writes and conditions unchanged.
- `GroupReservationService.SubmitGroupPreOrderAsync` catches only a `TransactionCanceledException` whose `CancellationReasons` entry at index 1 has code `ConditionalCheckFailed`, then reloads the group with `IGroupReservationRepository.GetGroupReservationByIdAsync` and throws the existing exception for the actual state:
  - `Submitted` -> `GroupReservationSubmittedException`
  - `Closed` -> `GroupReservationExpiredException`
  - not found -> `GroupReservationNotFoundException`
  - still `Open` (cause was not the group status) -> `ValidationException` with a clear message (concurrent modification, retry).
- If `CancellationReasons` is null, does not contain index 1, or index 1 is not `ConditionalCheckFailed`, rethrow the same `TransactionCanceledException` unchanged without reloading the group.
- HTTP mappings stay as they are today (`GroupReservationSubmittedException`/`GroupReservationExpiredException`/`ValidationException` -> 400 via `ResponseBuilder.CreateErrorCodeResponse`, `GroupReservationNotFoundException` -> 404). No new status codes, no new exception types or abstractions, no interface changes.

### Concurrent order updates (explicitly out of scope)
- Do not change the order write, add concurrency protection, or modify the `Order` entity/schema. Document only the known limitation: concurrent order edits can be overwritten; the group condition prevents duplicate submission.

## Hard constraints (unchanged)
- No database, table, index, schema or entity changes; no new infrastructure.
- No changes to `infra/terraform`, IAM, any `deployment_resources.json`, `lambda_config.json`, environment variables.
- No refactoring or behavior changes outside finding 2. Finding 1 (file moves), finding 3 (order concurrency protection), null `RequestContext` handling, zero-quantity filtering, and other review comments are out of scope.

## Open questions
- None. Finding 1 is skipped. Finding 3 remains a limitation and will not be implemented.

## Affected files and resources
- New: none.
- Changed: `Repositories/Orders/OrderRepository.cs` (remove broad exception mapping), `Services/GroupReservations/GroupReservationService.cs` (filter cancellation reason index 1, reload and map only the GroupReservation condition failure).
- Tests: update/add only affected cases in `Tests/Services/GroupReservationServiceTests.cs`; verify `Submitted`, `Closed`, not found and still `Open`, plus unchanged rethrow and no group reload for unrelated/missing cancellation reasons.
- Docs: none expected.
- Not affected: entities, `lambda_config.json`, `deployment_resources.json`, `infra/terraform`, `.sln`.

## Tasks
- [x] 1. AWS Lambda Agent (implementation, no tests)
  - Input: this plan and the previous plan. First inspect the current code and confirm the proposed minimal changes; if anything differs or more is needed, stop and report. Implement finding 2 only. Do not move files or modify order concurrency behavior. Run `dotnet build restaurant-backend-app.sln`. Report changed files and any limitation immediately. No refactoring, new abstractions or unrelated edits.
  - Expected: solution build passes; only the two targeted production files change.
- [x] 2. Unit Test Agent (tests only)
  - Input: this plan and task 1 result. Adapt/add only tests for finding 2: reload mapping for Submitted/Closed/not found/still Open, cancellation reason index 1 filtering, no group reload for unrelated or missing reasons, and unchanged exception rethrow. Run `dotnet test` and confirm all 206 existing tests plus the new tests pass.
  - Expected: the full suite passes, including all 206 baseline tests and the focused new tests; no production code changes in this task.
- [x] 3. Code Review Agent (read-only)
  - Input: this plan and all changed files. Review the COMPLETE Git diff (`git status` and `git diff`), flag anything outside the narrow scope, verify index-1 cancellation handling and unchanged rethrows, confirm finding 3 was not implemented, check no DB/entity/infra/IAM/Terraform/deployment files changed, and run `dotnet build restaurant-backend-app.sln` and `dotnet test`.
  - Expected: verdict and findings by severity.
- [x] 4. Fix loop not needed (review verdict approve with comments; no Critical/Major findings).

## Results

### Task 1 - AWS Lambda Agent: done
- Outcome: `dotnet build restaurant-backend-app.sln` succeeded (unrelated package vulnerability/compiler warnings reported); tests not run in this task.
- Removed the repository's broad transaction cancellation mapping. The service reloads and maps status only when cancellation reason index 1 is exactly `ConditionalCheckFailed`; absent/missing/nonmatching reasons rethrow unchanged without reload.
- Changed only `dnapp/lambdas/api-handler/Repositories/Orders/OrderRepository.cs` and `dnapp/lambdas/api-handler/Services/GroupReservations/GroupReservationService.cs`.
- Finding 1 remained skipped. Finding 3 remains a limitation: concurrent order edits can be overwritten; no order write or entity/schema change was made.

### Task 2 - Unit Test Agent: done
- `dotnet test Tests/APIHandler.Tests.csproj` and `dotnet test restaurant-backend-app.sln`: 213 passed / 0 failed / 0 skipped (206 baseline + 7 net new cases).
- Added/updated service cases for reason index 1 condition failure mapping to Submitted, Closed, not found and still Open; null/missing reasons, other index-1 codes and index-0-only failure rethrow the same transaction exception without a group reload; failure paths do not invoke separate order/group saves.
- No production issue reported. Changed only `Tests/Services/GroupReservationServiceTests.cs`.

### Task 3 - Code Review Agent: approve with comments
- Scoped logic: no correctness findings. Index 1 exactly `ConditionalCheckFailed` is the only path that reloads the group and maps its current state; all other/missing reasons rethrow unchanged.
- Validation: full solution build succeeded with 58 warnings (including existing NU190x and compiler/analyzer warnings). Full-solution tests passed in Task 2: 213 passed / 0 failed / 0 skipped.
- Scope: no Critical/Major findings. No changes under DB/entities, `infra/terraform`, IAM, deployment resources or environment configuration. Finding 1 stayed skipped; finding 3 stayed unchanged.
- Reviewer noted unrelated, ownership-uncertain worktree changes, left untouched: `.github/agents/*` including deleted `hello-world.agent.md`; prior backend changes from group sessions and earlier endpoint work (`CHANGELOG.md`, `GuestSessionServiceTests.cs`, handler/model/interface/route/service/docs/swagger files); `frontend/restaurant-frontend-app/package-lock.json`; untracked `Tests/Handlers/`, `homework-design/`, `homework-frontend.md`, `practice-frontend.md`, and earlier plan files. These are outside this task's diff and were not reverted or edited.
- Complete Git status/diff inventory was reviewed by the agent; files inspected included the current plan, `OrderRepository.cs`, `GroupReservationService.cs`, `GroupReservationServiceTests.cs`, `IOrderRepository.cs`, plus changed-path inventory. No review files were modified.
- Verdict: approve with comments. Known limitation: concurrent order edits can be overwritten because the order write is unconditional; this was explicitly not changed.
