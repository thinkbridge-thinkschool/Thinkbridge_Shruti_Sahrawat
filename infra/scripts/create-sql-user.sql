-- =============================================================================
-- The one step in this stack that is not Bicep, and cannot be.
--
-- Bicep creates the SQL server, the database, and the managed identity. What it
-- cannot create is the identity's *user inside the database* - that is T-SQL,
-- executed against the database itself by a connection holding an Entra ID admin
-- token. There is no ARM resource for a database principal, so an IaC repo that
-- claims "no click-ops" and quietly leaves this out has just moved the manual
-- step somewhere less visible than the portal.
--
-- Run it once per database, after `az deployment sub create` and before the API
-- is expected to serve a request:
--
--   sqlcmd -S <server>.database.windows.net -d quotesdb -G -v identityName="<mi-name>" -i infra/scripts/create-sql-user.sql
--
-- -G authenticates with the Entra ID account in your az login session, which has
-- to be the server's Entra admin (main.bicep sets that from sqlAadAdminObjectId).
--
-- Idempotent: safe to run again, and re-running is the intended response to
-- "I am not sure whether this already ran".
-- =============================================================================

SET NOCOUNT ON;

DECLARE @identityName sysname = N'$(identityName)';
DECLARE @sql nvarchar(max);

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @identityName)
BEGIN
    -- FROM EXTERNAL PROVIDER binds this database user to the Entra ID object,
    -- not to a password. It matches on the identity's *name*, and Azure SQL
    -- resolves that to the object ID at creation time - which is why the
    -- identity is user-assigned (modules/identity.bicep): a system-assigned
    -- identity is destroyed with its container app, and the next deployment
    -- creates a different object ID that this user no longer matches, with no
    -- error until the first query fails at runtime.
    SET @sql = N'CREATE USER ' + QUOTENAME(@identityName) + N' FROM EXTERNAL PROVIDER;';
    EXEC sp_executesql @sql;
    PRINT 'Created database user ' + @identityName + '.';
END
ELSE
BEGIN
    PRINT 'Database user ' + @identityName + ' already exists - nothing to do.';
END

-- db_datareader + db_datawriter, not db_owner. The API reads and writes rows; it
-- does not need to create tables at runtime... with one exception that is worth
-- naming rather than hiding: Database:SchemaBootstrap=Migrate means EF applies
-- migrations on startup, and that needs DDL rights. Two honest options:
--
--   a) grant db_ddladmin as well, and accept that the API can alter its own
--      schema - simple, and what this script does by default; or
--   b) drop the Migrate setting, run `dotnet ef database update` from CI with a
--      deploy principal, and leave the API with reader/writer only.
--
-- (b) is the better answer for anything with real data in it. (a) is what this
-- stack does today, and the comment is here so the choice is visible the next
-- time somebody reads this file wondering why an app has DDL rights.
DECLARE @roles table (name sysname);
INSERT INTO @roles (name) VALUES (N'db_datareader'), (N'db_datawriter'), (N'db_ddladmin');

DECLARE @role sysname;
DECLARE role_cursor CURSOR FOR SELECT name FROM @roles;
OPEN role_cursor;
FETCH NEXT FROM role_cursor INTO @role;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM sys.database_role_members rm
        JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
        JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id
        WHERE r.name = @role AND m.name = @identityName
    )
    BEGIN
        SET @sql = N'ALTER ROLE ' + QUOTENAME(@role) + N' ADD MEMBER ' + QUOTENAME(@identityName) + N';';
        EXEC sp_executesql @sql;
        PRINT 'Added ' + @identityName + ' to ' + @role + '.';
    END
    ELSE
    BEGIN
        PRINT @identityName + ' is already a member of ' + @role + '.';
    END

    FETCH NEXT FROM role_cursor INTO @role;
END

CLOSE role_cursor;
DEALLOCATE role_cursor;
