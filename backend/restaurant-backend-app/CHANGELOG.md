# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - yyyy-MM-dd
### Added
    -  Added items
    -  `GET /guest/groups/{groupReservationId}/sessions` - Owner-only list of group participant sessions with items, per-participant and overall totals
    -  `GET /reservations/{reservationId}/guest-session-id` - Owner-only; returns the owner's guest session id (creates it if missing, 404 when no group reservation exists)
    -  `POST /group-reservations/{groupReservationId}/submit` - Owner-only; merges dishes of submitted guest sessions into the order and submits the group pre-order atomically; returns the consolidated `[{dishId, quantity}]` list

### Changed
    -  Changed items 

### Removed
    -  Removed items 
