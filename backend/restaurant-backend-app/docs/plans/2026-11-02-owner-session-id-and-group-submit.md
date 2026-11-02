# Plan: Owner session id and consolidated group pre-order submit

Status: Done

## Goal
The reservation Owner can (1) get the id of their own guest session for a reservation and (2) submit the consolidated group pre-order built from all `submitted` guest sessions.

## Requirements
### 1. `GET /reservations/{reservationId}/guest-session-id`
- 200: `{ "guestSessionId": "owner-session-123" }`.
- Only the reservation Owner (`Reservation.CustomerId == caller sub`) may call it.
- 401 not authenticated; 403 caller is not the Owner; 404 reservation does not exist; 404 no GroupReservation exists for the reservation (GET never creates a GroupReservation).
- If the GroupReservation exists but has no Owner session, create it with the existing `CreateGroupOwnerSessionAsync`; if one already exists, return its id (no duplicate).

### 2. `POST /group-reservations/{groupReservationId}/submit`
- Request: `{ "reservationId": "reservation-123" }`.
- Only the reservation Owner may call it (401 / 403 as above).
- Steps:
  1. Take only guest sessions of the group with `IsSubmitted == true`.
  2. Collect all dishes of these sessions into one list.
  3. Merge identical dishes by `dishId`, summing quantities.
  4. Add the consolidated dishes to the order.
  5. Set order status to `SubmittedByCustomer`.
  6. Set group reservation status to `Submitted`.
- Response 200: `[ { "dishId": "dish1", "quantity": 3 }, { "dishId": "dish2", "quantity": 2 } ]`.
- Errors: 404 reservation or group reservation not found; 400 body `reservationId` missing or different from `GroupReservation.ReservationId`; 400 no `submitted` guest sessions; 400 submitted sessions contain no dishes; 400 order does not exist; 400 group reservation already `Submitted` (existing `GroupReservationSubmittedException`) or `Closed` (existing `GroupReservationExpiredException`).
- Check order in the service: group 404 -> reservation 404 -> Owner 403 -> body/`ReservationId` mismatch 400 -> group status 400 -> no submitted sessions 400 -> no dishes 400 -> order missing 400.
- Retry safety: after a successful submit the group is `Submitted`, so a repeat call is rejected with 400 before any change; quantities are never added twice.
- Persistence: order (merged dishes, `SubmittedByCustomer`) and group reservation (`Submitted`) are saved in ONE DynamoDB transaction (`IDynamoDBContext.CreateMultiTableTransactWrite`, AWSSDK.DynamoDBv2 4.x, two tables, per-table `OverrideTableName`), with a condition that the group item still has status `Open` so concurrent or retried calls fail instead of double-adding. Transactions need no new IAM actions (the api-handler policy already has PutItem/UpdateItem) and no table, index or schema change. If the implementer finds the atomic transaction cannot be done with the existing `IDynamoDBContext`, tables, entities and configuration, it must NOT change infrastructure, IAM or the database and must NOT silently fall back to a non-atomic save: it stops that part, reports the limitation, and the user decides (a possible non-atomic option, order first then group status guarded by the `Open` check, is only implemented after explicit approval).

## Database and infrastructure constraints (hard rules for all agents)
- Do not create, modify, migrate or restructure the database; no new DynamoDB tables or indexes; no changes to existing schemas, entity structures (`Order`, `GroupReservation`, `GuestSession`, `OrderItem`, no new attributes) or table configuration.
- Reuse existing tables, repositories, entities and persistence mechanisms (`IDynamoDBContext`, `ResourceNames`, `DynamoConfig`); no migrations or initialization scripts.
- Do not modify AWS infrastructure: `infra/terraform`, IAM policies, `deployment_resources.json` (root or per lambda), `lambda_config.json`, Lambda deployment resources, environment variables, `.syndicate-config*`.
- All changes stay inside the existing backend project (`api-handler`, `Tests`, `docs`, `swagger_src`, `CHANGELOG.md`).
- If a requirement (e.g. the atomic transaction) cannot be met within these limits, report the limitation instead of changing infrastructure or the database.

## Decisions (from user answers and constraints)
- GET never creates a GroupReservation (404). If the GroupReservation exists without an Owner session, create it via `CreateGroupOwnerSessionAsync` (change it to return the new session id; it is currently unused, no other callers) and expose it on `IGuestSessionService`; reuse an existing Owner session. Owner session is found among `GetAllByGroupReservationIdAsync(...)` with `IsOwnerSession`. Known limitation: two simultaneous first calls could create two Owner sessions (no conditional write on that table); accepted.
- Order dishes: merge into the existing order. Consolidated quantities are summed with dishes already in the order by `dishId`; existing items keep their `State` (only `Quantity` changes); new items use `OrderItemState.Pending`. Nothing else in the order is changed except `Status = SubmittedByCustomer`.
- Body `reservationId` MUST equal `GroupReservation.ReservationId`, otherwise 400 (supersedes the earlier "ignore body" decision).
- Service placement: GET logic in `GuestSessionService` (already has group, reservation and guest-session repositories). Submit logic in `GroupReservationService` (already has group, reservation, guest-session repositories; add `IOrderRepository` to its constructor). Repositories only, no new service, no dependency on `OrderService`/`GuestSessionService`, so no circular dependency. Atomic save lives in a repository method (e.g. on `IOrderRepository` or `IGroupReservationRepository`, implementer's choice, one new method only).
- No refactoring or behavior change outside these two endpoints; existing tests are only touched where a constructor signature changes.

## Assumptions (from code inspection)
- "Owner" = reservation owner (`Reservation.CustomerId`); there is no Owner Cognito role (`Roles`: Customer/Waiter/Admin). Existing pattern: `GuestSessionService.ValidateReservationOwnerAsync(groupReservationId, userId)` throws `GroupReservationNotFoundException`, `ReservationNotFoundException`, `ActionForbiddenException`.
- Existing pieces to reuse: `IGuestSessionRepository.GetAllByGroupReservationIdAsync`, `IOrderRepository.GetOrderByReservationIdAsync` / `SaveOrderAsync`, `IGroupReservationRepository.SaveGroupReservationAsync`, `OrderStatus.SubmittedByCustomer`, `GroupReservationStatus.Submitted`, `OrderItem` / `OrderItemState`, handler pattern of `GetGroupSessionsHandler`, `UserContextHelper.GetAuthenticatedUserId`.
- `CreateGroupOwnerSessionAsync` exists in `GuestSessionService` but is not on `IGuestSessionService` and is not called anywhere; it would need to be exposed on the interface.
- Repositories use `IDynamoDBContext` only (`SaveAsync` with `OverrideTableName`); no transactions or conditional writes exist yet. `OrderRepository.GetOrderByReservationIdAsync` returns the first non-`Cancelled` order.
- Persisting: a new transaction helper is new infrastructure in one repository method; `GroupReservationServiceTests.cs` and any constructor usage of `GroupReservationService` need updating for the new `IOrderRepository` dependency.
- Owner session (`IsOwnerSession`) is a guest session; if it is `IsSubmitted` it is included in the consolidation like any other session.
- Order is looked up by `reservationId` (group's parent reservation); missing order -> `ValidationException` (400).
- A group reservation already in `Submitted` status raises existing `GroupReservationSubmittedException` (400); `Closed` raises `GroupReservationExpiredException` (400).
- JSON uses camelCase via `ResponseBuilder.CreateJsonResponse`.
- No new AWS resources, env variables, IAM, table or index changes (DynamoDB tables for orders, group reservations and guest sessions are already used by `api-handler`); see the constraints section.
- Docs mismatch to fix: `docs/ApiRoutes.md` already lists `/group-reservations/{id}/submit` (response `{ "message": ... }`) and `/reservations/{id}/guest-session-id`; swagger lists the submit path as `/guest/groups/{groupReservationId}/submit`. The required path is `/group-reservations/{groupReservationId}/submit`.

## Open questions
1. RESOLVED: GET returns 404 when no GroupReservation exists; it never creates one.
2. Should a reservation or order in a non-active state (e.g. order `Cancelled`, reservation not `Reserved`) block the submit? Not in the requirements; proposed default: no extra checks beyond those listed.
3. API Gateway authorizer/route config for the two new routes is not in this repo (outside `infra/terraform`?); to be confirmed manually.

## Affected files and resources
- New: `dnapp/lambdas/api-handler/Handlers/GuestSessions/GetOwnerSessionIdHandler.cs`, `Handlers/GroupReservations/SubmitGroupPreOrderHandler.cs`, request/response models under `Models/GuestSessions/` and `Models/GroupReservation/` (e.g. `GetOwnerSessionIdResponse`, `SubmitGroupPreOrderRequest`, `SubmitGroupPreOrderItem`)
- Changed: `Services/GuestSessions/IGuestSessionService.cs`, `Services/GuestSessions/GuestSessionService.cs` (owner session id; `CreateGroupOwnerSessionAsync` returns the id), `Services/GroupReservations/IGroupReservationService.cs` / `GroupReservationService.cs` (submit logic, new `IOrderRepository` dependency), one repository method for the atomic save (`Repositories/Orders/*` or `Repositories/GroupReservations/*`), `Routing/RouteRegistry.cs`
- Tests: `Tests/Services/GuestSessionServiceTests.cs`, tests for the submit service, new `Tests/Handlers/GetOwnerSessionIdHandlerTests.cs`, `Tests/Handlers/SubmitGroupPreOrderHandlerTests.cs`
- Docs: `docs/ApiRoutes.md`, `swagger_src/swagger-ui/swagger-endpoints.yaml`, `CHANGELOG.md`
- Not affected (must stay untouched): `lambda_config.json`, `deployment_resources.json` (root and per lambda), `infra/terraform`, entity classes, `.sln`.

## Tasks
- [x] 1. AWS Lambda Agent (implementation, no tests)
  - Input: this plan. Implement both endpoints per Requirements and Decisions: handlers, service methods (interface and implementation), models, routes in `RouteRegistry`, merge logic, status updates; update `docs/ApiRoutes.md`, swagger (fix submit path to `/group-reservations/{groupReservationId}/submit`, add 401/403/404 where applicable) and `CHANGELOG.md`. Report replaced docs text and changed files. Run `dotnet build restaurant-backend-app.sln`. Use edit tools, not terminal, for file changes. Do not refactor or change unrelated code; keep to the existing service/handler/repository layering. Obey the database and infrastructure constraints: no DB/entity/index/IAM/Terraform/deployment/env changes; if the atomic transaction is impossible within them, stop that part and report the limitation (no silent non-atomic fallback).
  - Expected: both endpoints implemented; build passes.
- [x] 2. Unit Test Agent (tests only)
  - Input: this plan and files from task 1. Cover: GET happy path, owner session created when missing, existing session reused (no create call), no GroupReservation (404, nothing created), reservation not found (404), not Owner (403), 401; submit happy path with merged quantities, only `submitted` sessions used, merge with existing order dishes keeping existing item states and new items `Pending`, no submitted sessions (400), submitted sessions without dishes (400), order missing (400), body `reservationId` mismatch (400, nothing saved), group already `Submitted` (400, nothing saved, quantities not duplicated on second call), reservation/group not found (404), not Owner (403), atomic save called once with order `SubmittedByCustomer` and group `Submitted`, handler status mapping. Update existing tests broken by the `GroupReservationService` constructor change. Run `dotnet test`.
  - Expected: new tests pass; no production code changed.
- [x] 3. Code Review Agent (read-only)
  - Input: this plan and all changed files.
  - Expected: verdict and findings by severity.
- [x] 4. Fix loop: not needed (verdict approve with comments, no Critical/Major findings; 0 of 2 iterations used).

## Results

### Task 3 - Code Review Agent: approve with comments
- `dotnet build` .sln: 0 errors; `dotnet test`: 206 passed / 0 failed. No Critical or Major findings. No changes under `infra/terraform`, IAM, `deployment_resources.json`, `lambda_config.json`, entities or schema (checked with git).
- Logic, check order, merge, state preservation, retry safety, DI and docs/swagger/CHANGELOG verified against the plan. Transaction reuses context converters and existing tables.
- Minor: (1) transaction in `OrderRepository.SaveOrderAndSubmitGroupAsync` has no automated test, never run against DynamoDB - add mocked-context test or smoke test (submit twice: 200 then 400); (2) any `ConditionalCheckFailed` maps to "already submitted"; (3) order is saved as an unconditional `Put` (concurrent waiter edit could be overwritten); (4) `UserContextHelper.GetAuthenticatedUserId` throws `NullReferenceException` for null `RequestContext` (pre-existing helper, ~15 peer handlers use it; new handlers inconsistent with `GetGroupSessionsHandler`); (5) zero/negative quantity items not filtered; (6) one weak test (`NotOwnerAndGroupSubmitted_ThrowsForbiddenBeforeStatusCheck`); (7) duplicated Owner check across two services (accepted by plan).
- Convention (rated 2/5): new classes were appended to existing files instead of one class per file. Recommended split before merge: `GetOwnerSessionIdHandler.cs`, `SubmitGroupPreOrderHandler.cs`, `GetOwnerSessionIdResponse.cs`, `SubmitGroupPreOrderRequest.cs`, `SubmitGroupPreOrderItem.cs`, and the two handler test classes.
- Suggestions: comment on `ToStatusAttribute` helper; swagger documents 400 as `ErrorResponse` though domain 400s return `ErrorCodeResponse` (same as peer endpoints).
- Questions: reviewer saw older diff (moved `GetGroupSessionsAsync`, `LambdaLogger`, `/sessions` docs) that appears to belong to the 2026-11-01 plan; API Gateway route/authorizer config outside this repo (open question 3).
- Files inspected: all files listed in Task 1 and Task 2 results; none modified.

### Task 2 - Unit Test Agent: done
- Outcome: `dotnet test` 206 passed / 0 failed / 0 skipped (48 new). No production code changed.
- Fixed `GroupReservationServiceTests.cs` constructor (added `IOrderRepository` mock); existing tests untouched.
- Added: 7 service tests for `GetOwnerSessionIdAsync` / `CreateGroupOwnerSessionAsync`; 20 for `SubmitGroupPreOrderAsync` (merge, only submitted sessions, existing item states kept, new items Pending, 400/403/404 cases with nothing saved, second call rejected, atomic save called once, condition failure propagated); 7 handler tests for `GetOwnerSessionIdHandler`; 14 for `SubmitGroupPreOrderHandler`.
- Not covered: `TransactionCanceledException` -> `GroupReservationSubmittedException` mapping inside `OrderRepository.SaveOrderAndSubmitGroupAsync` (needs deep `IDynamoDBContext` mock); group `Submitted` status set is verified only indirectly (repository method takes the group id).
- Production issues reported: (low) `UserContextHelper.GetAuthenticatedUserId` throws `NullReferenceException` when `request.RequestContext` is null, used by both new handlers (`GetGroupSessionsHandler` returns 401 instead); condition failure always maps to "already submitted".
- Deviation: new test classes appended to existing files (no file-creation tool): `Tests/Handlers/GetGroupSessionsHandlerTests.cs` holds the two new handler test classes, instead of the planned separate files.
- Changed (under `backend/restaurant-backend-app/Tests/`): `Services/GroupReservationServiceTests.cs`, `Services/GuestSessionServiceTests.cs`, `Handlers/GetGroupSessionsHandlerTests.cs`. No files created.

### Task 1 - AWS Lambda Agent: done
- Outcome: both endpoints implemented; `dotnet build` of `APIHandler.csproj`: 0 errors. `.sln` build: 1 expected error, `Tests/Services/GroupReservationServiceTests.cs` line 30 (CS7036, missing `IOrderRepository` constructor argument) - left for the Unit Test Agent.
- GET: `GuestSessionService.GetOwnerSessionIdAsync` (on `IGuestSessionService`); order reservation 404 -> Owner 403 -> group 404; reuses or creates the Owner session; `CreateGroupOwnerSessionAsync` now on the interface and returns the id.
- POST: `GroupReservationService.SubmitGroupPreOrderAsync` (new `IOrderRepository` dependency); atomic save `IOrderRepository.SaveOrderAndSubmitGroupAsync` via `CreateMultiTableTransactWrite` with `status` condition (group still `Open`); condition failure -> `GroupReservationSubmittedException` (400). Transaction was feasible within existing DB/config; no limitation to report.
- Deviation: agent had no file-creation tool, so new classes were appended to existing files instead of new files: `GetOwnerSessionIdHandler` in `Handlers/GuestSessions/GetGroupSessionsHandler.cs`, `SubmitGroupPreOrderHandler` in `Handlers/GroupReservations/GetInviteTokenHandler.cs`, `GetOwnerSessionIdResponse` in `Models/GuestSessions/CreateSessionResponse.cs`, `SubmitGroupPreOrderRequest`/`SubmitGroupPreOrderItem` in `Models/GroupReservation/GetGroupInviteTokenResponse.cs`. For the reviewer to judge (file-per-class convention).
- Known: condition-failure always maps to "already submitted" even if the group was closed concurrently; two simultaneous first GET calls could create two Owner sessions (accepted).
- Changed (under `backend/restaurant-backend-app/`): `dnapp/lambdas/api-handler/Models/GroupReservation/GetGroupInviteTokenResponse.cs`, `Models/GuestSessions/CreateSessionResponse.cs`, `Services/GuestSessions/IGuestSessionService.cs`, `Services/GuestSessions/GuestSessionService.cs`, `Services/GroupReservations/IGroupReservationService.cs`, `Services/GroupReservations/GroupReservationService.cs`, `Repositories/Orders/IOrderRepository.cs`, `Repositories/Orders/OrderRepository.cs`, `Handlers/GuestSessions/GetGroupSessionsHandler.cs`, `Handlers/GroupReservations/GetInviteTokenHandler.cs`, `Routing/RouteRegistry.cs`, `docs/ApiRoutes.md`, `swagger_src/swagger-ui/swagger-endpoints.yaml`, `CHANGELOG.md`. No files created.
- Old text replaced (restore points):
  - `docs/ApiRoutes.md`: `GET /reservations/{reservationId}/guest-session-id` -> `Response: 200 OK`, `{ "guestSessionId": "string" }`; `POST /group-reservations/{groupReservationId}/submit` -> `Response: 200 OK`, `{ "message": "Group reservation pre-order submitted successfully." }`.
  - swagger: path `/guest/groups/{groupReservationId}/submit`; description `Submits the pre-order for a group reservation, converting guest sessions to orders`; 200 schema array of `type: object` with description `Enriched dishes data`; 400 description `Invalid request - missing parameters or validation error`; no security and no 401/403/404. `getOwnerSessionId` description `Retrieves the guest session ID for the reservation owner`, 404 `Reservation not found`, no 401/403. `SubmitGroupPreOrderRequest.reservationId` description `The ID of the reservation (owner of the group)`.
  - `CHANGELOG.md`: two lines added under `Added`, nothing replaced.