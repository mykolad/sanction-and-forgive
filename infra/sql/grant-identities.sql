-- What Bicep can't do: database users for the managed identities. Idempotent; run as the server's Entra admin with
-- infra/sql/Invoke-Sql.ps1 (infra/README.md, "Bootstrap"). No SQL logins exist: the server is Entra-only.

-- The app and the import job (one identity): read and write lists, no schema changes.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'sanction-and-forgive-app')
    CREATE USER [sanction-and-forgive-app] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [sanction-and-forgive-app];
ALTER ROLE db_datawriter ADD MEMBER [sanction-and-forgive-app];

-- The deploy workflow: runs the EF migration bundle.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'sanction-and-forgive-github-cd')
    CREATE USER [sanction-and-forgive-github-cd] FROM EXTERNAL PROVIDER;
ALTER ROLE db_ddladmin ADD MEMBER [sanction-and-forgive-github-cd];
ALTER ROLE db_datareader ADD MEMBER [sanction-and-forgive-github-cd];
ALTER ROLE db_datawriter ADD MEMBER [sanction-and-forgive-github-cd];
