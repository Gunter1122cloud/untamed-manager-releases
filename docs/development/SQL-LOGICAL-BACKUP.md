# Logical SQL backup and empty-target restore

The backup screen now exposes an explicit MariaDB CLI adapter. It does not discover or start services. The operator supplies matching 10.11.19 tools, an explicit loopback port, the expected data directory and a dedicated account restricted to the three supported databases.

Backup streams a single InnoDB transaction dump into a private staging directory. It includes routines, events and triggers, checks schema fingerprints before and after the dump, and publishes a manifest, SHA-256 and completion marker only after success. Temporary client options use a directory restricted to the current Windows account; credentials are not process arguments or logs.

Restore verifies package ownership and integrity, holds the SQL input against writes during import and refuses any target containing even one selected database. Accounts with global privileges, unrelated database privileges, roles or delegation are rejected. A failed import requires operator review; there is no automatic database deletion or retry. This is a logical SQL snapshot, not a coordinated ASA/character/Store recovery point. Existing databases cannot be replaced through this action.

`wpf/Testing/MariaDb/Run-RealSql.ps1` runs only on a separate GitHub Windows runner. It verifies the pinned official MariaDB package checksum, starts two disposable engines without service installation, and checks actual restored Unicode/blob rows, views, routines, triggers and events, existing-target rejection and archive corruption rejection.

The previous ASA test phase is administratively closed at the user's request. Unrun real ASA acceptance cases remain unverified; they are not recorded as successful tests. Production validation remains false.
