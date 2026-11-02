# Plan: Delete guest session from group pre-order

Status: Done

## Goal
When the reservation Owner deletes a guest session from a group pre-order, remove the session and return the remaining participants with accurate dish items and recalculated participant/group totals.

## Existing code inspection
- The endpoint is already registered: `DELETE /guest/groups/{groupReservationId}/sessions/{guestSessionId}` in `Routing/RouteRegistry.cs`.
- `Handlers/GuestSessions/DeleteGuestSessionHandler.cs` already extracts both path values, returns 401 when `sub` is absent, calls `IGuestSessionService.DeleteGuestSessionAsync`, and maps missing guest/group/parent reservation to 404, non-owner to 403, and validation errors to 400.
- `IGuestSessionService.DeleteGuestSessionAsync` already exists. `GuestSessionService.DeleteGuestSessionAsync` validates the Owner, loads a session by id, returns 404 if missing, checks that the session belongs to the specified group (400 otherwise), deletes it via the repository, then builds a group snapshot.
- `IGuestSessionRepository.GetByGuestSessionId`, `GetAllByGroupReservationIdAsync`, and `DeleteAsync` exist; `GuestSessionRepository.DeleteAsync` deletes the existing GuestSession DynamoDB item. No repository or schema work is needed.
- `GetGroupSessionsAsync` is the established snapshot implementation: it maps `IsOwnerSession` to `ParticipantType.Owner`, other sessions to `Guest`, maps dishes and prices, skips missing/deleted dishes, computes each participant total as sum of price x quantity, and the overall total as the sum of participant totals.
- The delete method currently duplicates snapshot construction rather than using the established behavior. It labels all remaining sessions `Guest` (including an Owner session) and indexes `dishesById[d.DishId]`; a remaining session referencing a deleted dish can throw. Existing `GetGroupSessionsAsync` avoids both issues. This is the primary confirmed behavior gap.
- The endpoint is documented in `docs/ApiRoutes.md`, but the documented response is a `{ "message": ... }` object rather than the required snapshot. Swagger already describes a group snapshot generically, but does not define the response schema precisely.
- There are no tests for `DeleteGuestSessionAsync` or `DeleteGuestSessionHandler`; existing snapshot service and handler tests cover participant item mapping and totals.

## Requirements
- `DELETE /guest/groups/{groupReservationId}/sessions/{guestSessionId}` remains the route and only the reservation Owner can delete.
- Session missing -> 404; session belongs to another group -> 400.
- The Owner's own session (`IsOwnerSession == true`) must not be deleted. Only sessions where `IsOwnerSession == false` can be deleted. Use the existing `ValidationException` path, which maps to HTTP 400.
- Preserve current behavior for `IsSubmitted` and for group status `Submitted` or `Closed`; do not introduce new restrictions for these statuses.
- On success, delete the existing GuestSession item and return the snapshot of all remaining participants.
- Participant type/name/submitted state/items use the existing snapshot contract. Participant total = sum of dish price x quantity; group total = sum of remaining participant totals.
- Reuse the `GetGroupSessionsAsync` snapshot calculation, without creating a second independent implementation or changing the GET response behavior.
- Do not modify the consolidated Order when deleting any session.
- No database/entity/index/infrastructure changes; no new services/repositories/abstractions beyond a private helper only if needed to share the existing snapshot logic safely.

## Proposed minimal implementation
- Keep route registration, repository APIs and handler contract; they already exist.
- In `GuestSessionService`, after Owner and target-session validation, reject the Owner's own session using `ValidationException` before calling `DeleteAsync`. After successful deletion, return the remaining snapshot using shared snapshot-building behavior.
- Preferred minimal snapshot approach: factor the current `GetGroupSessionsAsync` snapshot body into a private method used by both GET and DELETE, so DELETE does not repeat the Owner/group validation and both paths preserve the same Owner/Guest typing, deleted-dish handling and total rules. The GET method's externally visible behavior must remain unchanged.
- Keep existing validation order: validate group and parent reservation/Owner first; then load target session (missing 404); then verify its `GroupReservationId` matches (400); then reject `IsOwnerSession`; then delete; then build the remaining snapshot.
- Do not add checks for `IsSubmitted`, `GroupReservationStatus.Submitted`, or `GroupReservationStatus.Closed`; preserve the current behavior, which allows deletion in those cases.
- Update `docs/ApiRoutes.md` response to the snapshot JSON and update Swagger's 200 response schema to `GetAllSessionsResponse` (or its existing equivalent), including `participants`, participant `id/type/name/items/total/isSubmitted`, and root `total`. Do not change unrelated docs.
- No route or persistence changes are expected. The consolidated Order is never loaded or modified by this endpoint.

## Deletion impact
- Deletion is a hard delete of the GuestSession DynamoDB item. Its guest-session record, dish list and session token hash are removed together; no separate order item or other data is deleted by `DeleteAsync`.
- The existing consolidated Order is not modified, including when a submitted participant is deleted after group submission.
- Implication: deletion remains allowed for submitted sessions and groups in `Submitted` or `Closed` status; it removes the GuestSession and updates the returned snapshot only. A participant deleted after group submission may therefore still be represented in the existing consolidated Order. This is intentional under the current scope; no status restriction or order change is introduced.

## Authorization, validation and HTTP mappings
1. Missing path values: handler returns 400.
2. Missing authenticated `sub`: handler returns 401.
3. Group not found: service Owner validation throws `GroupReservationNotFoundException`; handler returns 404.
4. Parent reservation not found: `ReservationNotFoundException`; handler returns 404.
5. Caller is not reservation Owner: `ActionForbiddenException`; handler returns 403.
6. Target session not found: `GuestSessionNotFoundException`; handler returns 404.
7. Target session belongs to a different group: `ValidationException`; handler returns 400.
8. Target session is the Owner's own session (`IsOwnerSession == true`): `ValidationException`; handler returns 400 and the session is not deleted.
9. `IsSubmitted` or group status `Submitted`/`Closed`: retain current allowed behavior; do not add status validation.
10. Successful delete: handler returns 200 with the updated `GetAllSessionsResponse` snapshot.

## Tests
- Service success: delete called for the requested session and returns all remaining participants with correct names, Owner/Guest types, submitted flags, items, participant totals and overall total.
- Empty remaining group returns `participants: []`, `total: 0`.
- Deleted target's dishes are absent from returned totals; other participants remain.
- A remaining Owner session is reported as `Owner`, not `Guest`.
- A remaining item referencing a deleted dish is skipped and excluded from totals, matching `GetGroupSessionsAsync` behavior.
- Target session missing -> `GuestSessionNotFoundException`; assert no delete/snapshot query after failure.
- Target belongs to another group -> `ValidationException`; assert no delete.
- Target is the Owner session -> `ValidationException`; assert no delete.
- Group missing / parent reservation missing / non-Owner -> existing exceptions; assert no target deletion.
- Submitted target and group in `Submitted`/`Closed` statuses remain deletable (where test setup permits), confirming no new status restriction; verify that no consolidated Order is involved or modified.
- Handler maps missing paths 400, unauthenticated 401, forbidden 403, missing group/session/parent reservation 404, mismatched session and Owner-session deletion 400, and success 200 with camelCase snapshot JSON.
- Route remains discoverable exactly once in `RouteRegistry` if route-registry tests exist; otherwise no extra route test is required because the registration already exists.

## Open questions
- None for this scope. Owner-session deletion is explicitly forbidden; submitted/Submitted/Closed behavior is explicitly preserved; the consolidated Order is explicitly not modified.

## Affected files and resources
- Changed: `dnapp/lambdas/api-handler/Services/GuestSessions/GuestSessionService.cs`; `docs/ApiRoutes.md`; `swagger_src/swagger-ui/swagger-endpoints.yaml`.
- Tests: `Tests/Services/GuestSessionServiceTests.cs`; new `Tests/Handlers/DeleteGuestSessionHandlerTests.cs` (or existing handler test file only if repository conventions require it).
- Not expected to change: `Handlers/GuestSessions/DeleteGuestSessionHandler.cs`, `Services/GuestSessions/IGuestSessionService.cs`, `Repositories/GuestSessions/*`, `Entities/*`, `Routing/RouteRegistry.cs`, `lambda_config.json`, `deployment_resources.json`, `infra/terraform`, IAM or environment variables.

## Tasks
- [x] 1. AWS Lambda Agent (implementation, no tests)
  - Input: this plan. Reconfirm the existing behavior and proposed minimal change. Implement only the Owner-session rejection plus snapshot reuse and docs updates. Preserve current submitted/Submitted/Closed behavior; do not modify any Order. Reuse snapshot-building logic; preserve GET behavior. Update only the affected service and API docs/Swagger. No endpoint route duplication, schema or infrastructure change. Run `dotnet build restaurant-backend-app.sln`.
  - Expected: delete returns the same accurate snapshot shape as GET after deletion; build passes.
- [x] 2. Unit Test Agent (tests only)
  - Input: this plan and Task 1 changes. Add focused service/handler tests for success, totals, Owner type, deleted dish handling, Owner-session deletion rejection, unchanged submitted/Submitted/Closed behavior, empty snapshot and error mappings; no production changes. Run `dotnet test`.
  - Expected: tests pass and existing behavior remains covered.
- [x] 3. Code Review Agent (read-only)
  - Input: this plan and all changed files. Review correctness/scope, status-rule behavior, docs and the complete Git diff; run full build and tests.
  - Expected: verdict and findings by severity.
- [x] 4. Review follow-up completed; no further fix iteration requested.

## Results

### Task 1 - AWS Lambda Agent: done
- `dotnet build restaurant-backend-app.sln` succeeded with 0 errors; existing package vulnerability/compiler warnings reported. Tests not run in this task.
- DELETE rejects `IsOwnerSession == true` with existing `ValidationException` after Owner and group checks and before deletion.
- DELETE snapshot uses the same builder as GET, preserving participant types, missing-dish skipping and totals; GET behavior unchanged.
- No `IsSubmitted` or `Submitted`/`Closed` restrictions added; consolidated Order unchanged.
- Docs updated to show the snapshot response; Swagger 200 now references `GetAllSessionsResponse`.
- Changed: `dnapp/lambdas/api-handler/Services/GuestSessions/GuestSessionService.cs`, `docs/ApiRoutes.md`, `swagger_src/swagger-ui/swagger-endpoints.yaml`.
- Old docs restore points: ApiRoutes DELETE response was `{ "message": "Guest session removed from group successfully." }`; Swagger DELETE 200 schema was generic `type: object` with description `Updated snapshot of the group reservation`.

### Task 2 - Unit Test Agent: initial run found response casing bug
- Initial `dotnet test Tests/APIHandler.Tests.csproj`: 234 passed / 1 failed / 0 skipped. The new handler test found DELETE used `CreateSuccessResponse`, producing PascalCase instead of required camelCase.
- Production fix delegated to AWS Lambda Agent: `DeleteGuestSessionHandler` now uses existing `ResponseBuilder.CreateJsonResponse(200, updatedSnapshot)`; no other behavior changed.
- Lambda Agent build: `dotnet build restaurant-backend-app.sln` succeeded with 76 warnings. Tests still require rerun.
- Rerun after handler fix: `dotnet test Tests/APIHandler.Tests.csproj` passed 235 / failed 0 / skipped 0. No test edits were needed for the rerun.
- Test files created/changed: `Tests/Services/GuestSessionServiceTests.cs`, `Tests/Handlers/DeleteGuestSessionHandlerTests.cs`.

### Task 2 - Unit Test Agent: done
- Service and handler coverage added for updated snapshots, totals, Owner type, missing dishes, empty group, validation/authorization cases, Owner-session deletion rejection, and unchanged submitted/Submitted/Closed deletion behavior.
- Final `dotnet test Tests/APIHandler.Tests.csproj`: 235 passed / 0 failed / 0 skipped.
- A camelCase response defect found by the initial test was corrected in `Handlers/GuestSessions/DeleteGuestSessionHandler.cs` by using existing `CreateJsonResponse`; final test passed.
- No production changes by the test agent.

### Task 3 - Code Review Agent: approve with comments
- Full solution build succeeded (0 errors, 58 warnings); full solution tests passed (235 passed / 0 failed / 0 skipped).
- Behavior review: Owner-session deletion is rejected before deleting; submitted sessions and groups in `Submitted`/`Closed` remain allowed; no Order mutation; GET/DELETE share snapshot typing, deleted-dish handling and totals; success response is camelCase.
- Initial Minor finding: DELETE docs omitted existing 400/403/404 cases. Follow-up updated only the DELETE descriptions in `docs/ApiRoutes.md` and `swagger_src/swagger-ui/swagger-endpoints.yaml`; reviewer confirmed the status mapping list matches implementation. No build/test rerun after these docs-only changes.
- Scope caveat: reviewer found a broad shared worktree containing earlier/unrelated code changes and could not independently attribute every status path to this task. Changes were preserved; no unrelated files were reverted. No DB/entity/infra/IAM/Terraform/deployment/environment changes were identified.
- Follow-up verdict: approve with comments; remaining comment is only that current worktree status cannot prove attribution of all pre-existing code changes to individual tasks.
