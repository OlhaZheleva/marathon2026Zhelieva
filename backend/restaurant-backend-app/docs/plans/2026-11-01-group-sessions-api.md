# Plan: GET /guest/groups/{groupReservationId}/sessions

Status: Done

## Goal
Owner of a group reservation can list participant sessions of a group pre-order: items per participant, total per participant and overall total.

## Requirements
- `GET /guest/groups/{groupReservationId}/sessions`, response 200: `{ "participants": [ { id, type, name, items: [ { dish: { id, name, price }, quantity } ], total, isSubmitted } ], "total" }`.
- 401 if not authenticated; 403 if the user is not the Owner of this reservation; 404 if `groupReservationId` does not exist.
- Participant `total` = sum of `dish.price * quantity`; top-level `total` = sum of participant totals.
- `type` = `Guest` for guest sessions; the Owner's own session (if present) = `Owner`.
- No participants: 200 with `"participants": []`, `"total": 0`.
- Follow existing routing, DI, DynamoDB access and error response conventions in `api-handler`.
- Update `docs/ApiRoutes.md`, `swagger_src` and `CHANGELOG.md`.
- Unit tests: happy path with totals and types, empty group, group not found, forbidden for non-Owner, handler-level status mapping (401/403/404/200).

## Assumptions (from code inspection)
- No handler or route for this path exists in `api-handler` (`Handlers/GuestSessions/`, `Routing/RouteRegistry.cs`).
- Existing pieces to reuse: `IGuestSessionService` / `GuestSessionService` (private owner check `ValidateReservationOwnerAsync` throws `GroupReservationNotFoundException`, `ReservationNotFoundException`, `ActionForbiddenException`), `IGuestSessionRepository.GetAllByGroupReservationIdAsync`, `IDishRepository.GetDishesByIdsAsync`, models `GetAllSessionsResponse`, `Participant`, `ParticipantType` (`Models/GuestSessions/GetAllResponse.cs`), `DeleteGuestSessionHandler` as the handler pattern.
- `isSubmitted` comes from the existing `GuestSession.IsSubmitted` field; the Owner session is identified by `GuestSession.IsOwnerSession`.
- Responses should use camelCase JSON (`ResponseBuilder.CreateJsonResponse`).
- No new AWS resources, env variables or IAM changes are needed.

## Open questions
1. RESOLVED: Owner session `name` = the stored value (`GuestSession.GuestName`), no user lookup.
2. RESOLVED: `isSubmitted` = existing `GuestSession.IsSubmitted` field, no extra rule.
3. RESOLVED: Skip. A session item referencing a deleted dish is left out of `items` and excluded from participant and top-level totals; a warning is logged (session id and dish id only); no error, no placeholder; use `TryGetValue`.

Follow-up (out of scope): deleting a dish does not check guest sessions that still reference it.

## Affected files and resources
- New: `dnapp/lambdas/api-handler/Handlers/GuestSessions/GetGroupSessionsHandler.cs`
- Changed: `Services/GuestSessions/IGuestSessionService.cs`, `Services/GuestSessions/GuestSessionService.cs`, `Routing/RouteRegistry.cs`
- Tests: `Tests/Services/GuestSessionServiceTests.cs`, new handler tests (e.g. `Tests/Handlers/GetGroupSessionsHandlerTests.cs`)
- Docs: `docs/ApiRoutes.md`, `swagger_src/swagger-ui/swagger-endpoints.yaml`, `CHANGELOG.md`
- Not affected: `lambda_config.json`, `deployment_resources.json`, `infra/terraform`, `.sln`.

## Existing docs text (restore point)
`docs/ApiRoutes.md` currently documents this route with a different shape:
```
### `GET` /guest/groups/{groupReservationId}/sessions
Response: 200 OK
[ { "id": "string", "groupReservationId": "string", "guestName": "string", "status": "string" } ]
```
Swagger already has the path with `GetAllSessionsResponse` and 400/404 responses only. The AWS Lambda Agent must report the exact old text it replaces; it is recorded under Results.

## Tasks
- [x] 1. AWS Lambda Agent (implementation, no tests)
  - Input: this plan. Add service method, handler and route; update `ApiRoutes.md`, swagger (add 401/403) and `CHANGELOG.md`. Report the replaced docs text and changed files. Build with `dotnet build`.
  - Expected: endpoint implemented per requirements; build passes.
- [x] 2. Unit Test Agent (tests only)
  - Input: this plan and files from task 1. Cover happy path with totals and types, empty group, not found, forbidden, handler status mapping 401/403/404/200. Run `dotnet test`.
  - Expected: new tests pass, no production code changed.
- [x] 3. Code Review Agent (read-only)
  - Input: this plan and all changed files.
  - Expected: verdict and findings by severity.
- [x] 4. Fix loop (max 2 iterations) if the verdict is "request changes" or Critical/Major findings exist: code issues to AWS Lambda Agent, test issues to Unit Test Agent, then re-run the review.
  - [x] Iteration 1: AWS Lambda Agent (code and docs fixes)
  - [x] Iteration 1: Unit Test Agent (deleted-dish test)
  - [x] Iteration 1: Code Review Agent (re-review): approve with comments

## Results

### Re-review after fix iteration 1 - Code Review Agent: approve with comments
- Build 0 errors; `dotnet test` 158/158. No Critical or Major findings remain; all earlier findings resolved (Skip behavior with `TryGetValue`, swagger `OrderItemResponse`, docs example, deleted-dish tests).
- Remaining Minor (optional, not fixed): whitespace/blank-line churn in `GuestSessionService.cs` (two blank lines before `DeleteGuestSessionAsync`, blank line added before `SubmitCartAsync`); swagger `DishResponse` enums (`Main`, `OutOfStock`) do not match `DishMapper` labels (`"Main course"`, `"Out of stock"`), predates this work.
- Suggestion: static `LambdaLogger.Log` is not used elsewhere in api-handler (functionally fine, logs ids only).
- Open items: API Gateway authorizer config for the route could not be verified in the repo; 404 vs 403 reveals group existence (required by plan).

### Fix iteration 1 (of 2) - Unit Test Agent: done
- `dotnet test`: 158 passed / 0 failed / 0 skipped (2 new). No production code changed.
- New: `GetGroupSessionsAsync_ItemReferencesDeletedDish_SkipsItemAndExcludesItFromTotals`, `GetGroupSessionsAsync_OnlyItemReferencesDeletedDish_ReturnsParticipantWithEmptyItemsAndZeroTotal`; the 200 handler test now also asserts `participants[0].id` and `items[0].dish.id`. The warning log is not asserted (static `LambdaLogger`).
- Changed: `Tests/Services/GuestSessionServiceTests.cs`, `Tests/Handlers/GetGroupSessionsHandlerTests.cs`.

### Fix iteration 1 (of 2) - AWS Lambda Agent: done
- Build `dotnet build restaurant-backend-app.sln`: 0 errors; existing 156 tests still pass; no tests changed.
- Skip behavior: `GetGroupSessionsAsync` uses `TryGetValue`; missing dish is left out of `items` and both totals; logs `WARNING: Skipping missing dish {DishId} in guest session {SessionId}.` via static `LambdaLogger.Log` (new in api-handler; the service has no `ILambdaContext`; constructor unchanged). No exception.
- Swagger: `OrderItemResponse` now `{ dish: $ref DishResponse, quantity }` (only `Participant` referenced it). 404 text now `Group reservation (or its parent reservation) not found`. Existing `DishResponse` schema left untouched (its enums do not match `DishMapper` output; separate decision).
- `docs/ApiRoutes.md`: valid JSON example with Owner and Guest participants and the full dish object; text mentions Owner/Guest and skipped deleted dishes.
- Method order: `GetGroupSessionsAsync` now directly after `DeleteGuestSessionAsync` in `GuestSessionService.cs`; `IGuestSessionService.cs` needed no change; whitespace-only line removed. Note: the git baseline commit was made after the first (reverted) implementation, so `git diff` against it can still show the method as moved; relative to the original code the change is additive only.
- Process note: the agent used one `sed` terminal command to strip a whitespace-only line.
- Changed: `Services/GuestSessions/GuestSessionService.cs`, `swagger_src/swagger-ui/swagger-endpoints.yaml`, `docs/ApiRoutes.md`.
- Old text restore points: old swagger `OrderItemResponse` was the flat schema (`dishId`, `dishName`, `quantity`, `price`, `total`); old ApiRoutes example used `"string"` / `"Owner | Guest"` placeholders with a reduced dish object `{ id, name, price }`.

### Task 3 - Code Review Agent: request changes (fix iterations used: 1 of 2, in progress)
- Build 0 errors; `dotnet test` 156/156. Core requirements met.
- Major 1 (AWS Lambda Agent + Unit Test Agent): `GuestSessionService.cs` ~line 240, `dishesById[d.DishId]` throws `KeyNotFoundException` (HTTP 500) when a stored dish was deleted. Needs a behavior decision (open question 3), then code fix and a test.
- Major 2 (AWS Lambda Agent): swagger `OrderItemResponse` schema is flat (`dishId`, `dishName`, `quantity`, `price`, `total`) but the API returns `{ dish: DishResponse, quantity }`; fix the schema (check `DeleteGuestSessionHandler` does not depend on it).
- Minor (AWS Lambda Agent): `docs/ApiRoutes.md` example shows a reduced dish object, uses `"string"` / `"Owner | Guest"` placeholders instead of concrete JSON values; swagger 404 text mentions "GroupId is required" which does not apply; method order in `GuestSessionService.cs` / `IGuestSessionService.cs` swapped plus whitespace-only lines (unrelated churn).
- Minor (Unit Test Agent): add deleted-dish test once behavior is decided.
- Suggestions: stable participant order (Owner first), assert nested camelCase keys (`participants[0].id`, `items[0].dish.id`) in the 200 handler test; path-parameter check before 401 matches `DeleteGuestSessionHandler`.
- Review questions: API Gateway authorizer config for the route not verifiable in the repo; 404 vs 403 reveals group existence (plan requires both).

### Task 2 - Unit Test Agent: done
- Outcome: `dotnet test` passes, 156 passed / 0 failed / 0 skipped (16 new tests). No production code changed.
- Service tests (6, `GuestSessionServiceTests.cs`): happy path with totals and types, no owner session, empty group (dish repository not called), group not found, parent reservation missing, non-Owner forbidden.
- Handler tests (10, new `GetGroupSessionsHandlerTests.cs`): 400, 401 (3 variants), 403, 404 (2 variants), 200 with camelCase body, 200 empty group.
- Created: `Tests/Handlers/GetGroupSessionsHandlerTests.cs`; changed: `Tests/Services/GuestSessionServiceTests.cs`.
- Process note: the agent ran `mkdir Tests/Handlers` in the terminal (folder only).

### Task 1 - AWS Lambda Agent: done
- Outcome: endpoint implemented; `dotnet build` of the api-handler project: 0 errors, 42 pre-existing warnings. Full `.sln` not built; no tests run (as planned).
- Created: `dnapp/lambdas/api-handler/Handlers/GuestSessions/GetGroupSessionsHandler.cs`
- Changed: `Services/GuestSessions/IGuestSessionService.cs`, `Services/GuestSessions/GuestSessionService.cs`, `Routing/RouteRegistry.cs`, `docs/ApiRoutes.md`, `swagger_src/swagger-ui/swagger-endpoints.yaml`, `CHANGELOG.md`
- Process note: the agent created the handler file via a terminal heredoc instead of an edit tool; content was checked afterwards.
- Note: `dishesById[d.DishId]` throws `KeyNotFoundException` if a stored dish was deleted (same behavior as `DeleteGuestSessionAsync`); for the reviewer to judge.
- Old docs text replaced (restore point):
  - `docs/ApiRoutes.md`:
    ```
    ### `GET` /guest/groups/{groupReservationId}/sessions
    Response: 200 OK
    [ { "id": "string", "groupReservationId": "string", "guestName": "string", "status": "string" } ]
    ```
    (formatted as a json code block with multi-line fields)
  - swagger: description was `Retrieves all guest sessions for a group reservation`; responses had 400 then 404 only (404 text `Group reservation not found or GroupId is required` unchanged); agent inserted 401 and 403 between them.
  - `CHANGELOG.md`: nothing replaced; one line added under `### Added` after `-  Added items`.
