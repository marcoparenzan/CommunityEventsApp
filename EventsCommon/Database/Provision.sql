/*
    1nn0va Events - database script (the only one)

    Creates or updates everything the applications need in the database, and verifies the result.
    Use it on a NEW or EXISTING database (SQL Server, LocalDB, Azure SQL): it can be run again after every release.

    What it does, in order:
      1. creates the schema "community" and the base tables;
      2. creates the time zone setting (community.Settings) and the local-time functions
         community.LocalNow() / community.LocalNowDateTime(), used by the application for every date it creates;
      3. creates every other table, column, index and constraint, and applies the one-time conversions
         (default values to local time, UTC dates expressed with the local offset);
      4. makes sure the first SuperUser (marco.parenzan@1nn0va.it) exists and is enabled;
      5. at the end lists what is still missing (no row = the schema is complete).

    It NEVER deletes data. To empty the data of a development database use the reset endpoint of SpeakerApp
    (see docs/DATABASE.md).

    Run it with SSMS, Azure Data Studio or sqlcmd (-I): the script contains GO separators.
    Any change to the schema is made HERE, keeping the statements repeatable (IF NOT EXISTS / COL_LENGTH checks).
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF SCHEMA_ID(N'community') IS NULL
    EXEC(N'CREATE SCHEMA community');

-- Ora locale: le date create dall'applicazione sono in ora locale (UTC + fuso della community).
-- Il fuso (identificativo Windows) sta in community.Settings: lo imposta l'applicazione all'avvio da Application:TimeZone.
IF OBJECT_ID(N'community.Settings', N'U') IS NULL
    CREATE TABLE community.Settings
    (
        [Key] nvarchar(64) NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,
        [Value] nvarchar(256) NOT NULL
    );
IF NOT EXISTS (SELECT 1 FROM community.Settings WHERE [Key] = N'TimeZone')
    INSERT INTO community.Settings ([Key], [Value]) VALUES (N'TimeZone', N'W. Europe Standard Time');
IF OBJECT_ID(N'community.LocalNow', N'FN') IS NULL
    EXEC(N'CREATE FUNCTION community.LocalNow() RETURNS datetimeoffset AS
BEGIN
    DECLARE @tz nvarchar(128) = COALESCE((SELECT [Value] FROM community.Settings WHERE [Key] = N''TimeZone''), N''W. Europe Standard Time'');
    RETURN SYSDATETIMEOFFSET() AT TIME ZONE @tz;
END');
IF OBJECT_ID(N'community.LocalNowDateTime', N'FN') IS NULL
    EXEC(N'CREATE FUNCTION community.LocalNowDateTime() RETURNS datetime AS
BEGIN
    RETURN CONVERT(datetime, community.LocalNow());
END');

IF OBJECT_ID(N'community.Events', N'U') IS NULL
BEGIN
    CREATE TABLE community.Events
    (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Events PRIMARY KEY,
        [Year] int NOT NULL,
        Name nvarchar(128) NOT NULL,
        Description nvarchar(max) NOT NULL CONSTRAINT DF_Events_Description DEFAULT (N''),
        EventDate datetime NOT NULL,
        SessionizeMoniker nvarchar(64) NOT NULL CONSTRAINT DF_Events_SessionizeMoniker DEFAULT (N''),
        SessionizeJsonUri nvarchar(128) NOT NULL CONSTRAINT DF_Events_SessionizeJsonUri DEFAULT (N''),
        Moniker nvarchar(80) NOT NULL,
        IsCurrent bit NOT NULL CONSTRAINT DF_Events_IsCurrent DEFAULT (0),
        IsPublished bit NOT NULL CONSTRAINT DF_Events_IsPublished DEFAULT (1),
        CallForPapersOpenAt datetimeoffset NULL,
        CallForPapersCloseAt datetimeoffset NULL,
        CallForPapersUrl nvarchar(512) NOT NULL CONSTRAINT DF_Events_CfpUrl DEFAULT (N''),
        CheckInToken uniqueidentifier NOT NULL CONSTRAINT DF_Events_CheckInToken DEFAULT (NEWID()),
        IsCancelled bit NOT NULL CONSTRAINT DF_Events_IsCancelled DEFAULT (0),
        IsLocked bit NOT NULL CONSTRAINT DF_Events_IsLocked DEFAULT (0)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.Events') AND name = N'UX_Events_Year_Moniker')
    CREATE UNIQUE INDEX UX_Events_Year_Moniker ON community.Events ([Year], Moniker);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.Events') AND name = N'UX_Events_CheckInToken')
    CREATE UNIQUE INDEX UX_Events_CheckInToken ON community.Events (CheckInToken);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.Events') AND name = N'UX_Events_Current')
    CREATE UNIQUE INDEX UX_Events_Current ON community.Events (IsCurrent) WHERE IsCurrent = 1;

IF OBJECT_ID(N'community.Users', N'U') IS NULL
BEGIN
    CREATE TABLE community.Users
    (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
        LastName nvarchar(64) NULL,
        FirstName nvarchar(64) NULL,
        EMail nvarchar(64) NOT NULL,
        LastPin nvarchar(8) NULL,
        AllowEventsNotifications bit NOT NULL CONSTRAINT DF_Users_AllowEventsNotifications DEFAULT (1),
        RequestCancellation bit NOT NULL CONSTRAINT DF_Users_RequestCancellation DEFAULT (0),
        ToBeConfirmed bit NOT NULL CONSTRAINT DF_Users_ToBeConfirmed DEFAULT (0)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.Users') AND name = N'UX_Users_EMail')
    CREATE UNIQUE INDEX UX_Users_EMail ON community.Users (EMail);

IF OBJECT_ID(N'community.Speakers', N'U') IS NULL
BEGIN
    CREATE TABLE community.Speakers
    (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Speakers PRIMARY KEY,
        LastName nvarchar(64) NULL,
        FirstName nvarchar(64) NULL,
        EMail nvarchar(64) NOT NULL,
        LastPin nvarchar(8) NULL,
        SessionizeName nvarchar(64) NULL,
        IsSuperUser bit NOT NULL CONSTRAINT DF_Speakers_IsSuperUser DEFAULT (0),
        CanCheckIn bit NOT NULL CONSTRAINT DF_Speakers_CanCheckIn DEFAULT (0)
    );
END;

IF OBJECT_ID(N'community.UserEventRegistration', N'U') IS NULL
BEGIN
    CREATE TABLE community.UserEventRegistration
    (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_UserEventRegistration PRIMARY KEY,
        UserID int NOT NULL,
        EventID int NOT NULL,
        RegistrationDate datetime NOT NULL CONSTRAINT DF_UserEventRegistration_RegistrationDate DEFAULT (community.LocalNowDateTime()),
        [Status] nvarchar(50) NOT NULL CONSTRAINT DF_UserEventRegistration_Status DEFAULT (N'Pending'),
        TicketTokenHash binary(32) NULL,
        CheckedInAt datetimeoffset NULL,
        IsEatingPizza bit NOT NULL CONSTRAINT DF_UserEventRegistration_IsEatingPizza DEFAULT (0),
        LactoseIntolerant bit NOT NULL CONSTRAINT DF_UserEventRegistration_LactoseIntolerant DEFAULT (0),
        CONSTRAINT FK_UserEventRegistration_User FOREIGN KEY (UserID) REFERENCES community.Users(ID),
        CONSTRAINT FK_UserEventRegistration_Event FOREIGN KEY (EventID) REFERENCES community.Events(ID)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.UserEventRegistration') AND name = N'UX_UserEventRegistration_User_Event')
    CREATE UNIQUE INDEX UX_UserEventRegistration_User_Event ON community.UserEventRegistration (UserID, EventID);

IF OBJECT_ID(N'community.UserLogs', N'U') IS NULL
BEGIN
    CREATE TABLE community.UserLogs
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_UserLogs PRIMARY KEY,
        IdUser int NOT NULL,
        LogTypeId int NOT NULL,
        Description nvarchar(256) NULL,
        ValueString nvarchar(64) NULL,
        Timstamp datetimeoffset NOT NULL,
        CONSTRAINT FK_UserLogs_Users FOREIGN KEY (IdUser) REFERENCES community.Users(ID)
    );
END;

PRINT N'Base tables ready.';
GO

SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
IF SCHEMA_ID(N'community') IS NULL
    THROW 50000, 'Required schema community does not exist.', 1;

-- Ora locale: le date create dall'applicazione sono in ora locale (UTC + fuso della community).
-- Il fuso (identificativo Windows) sta in community.Settings: lo imposta l'applicazione all'avvio da Application:TimeZone.
IF OBJECT_ID(N'community.Settings', N'U') IS NULL
    CREATE TABLE community.Settings
    (
        [Key] nvarchar(64) NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,
        [Value] nvarchar(256) NOT NULL
    );
IF NOT EXISTS (SELECT 1 FROM community.Settings WHERE [Key] = N'TimeZone')
    INSERT INTO community.Settings ([Key], [Value]) VALUES (N'TimeZone', N'W. Europe Standard Time');
IF OBJECT_ID(N'community.LocalNow', N'FN') IS NULL
    EXEC(N'CREATE FUNCTION community.LocalNow() RETURNS datetimeoffset AS
BEGIN
    DECLARE @tz nvarchar(128) = COALESCE((SELECT [Value] FROM community.Settings WHERE [Key] = N''TimeZone''), N''W. Europe Standard Time'');
    RETURN SYSDATETIMEOFFSET() AT TIME ZONE @tz;
END');
IF OBJECT_ID(N'community.LocalNowDateTime', N'FN') IS NULL
    EXEC(N'CREATE FUNCTION community.LocalNowDateTime() RETURNS datetime AS
BEGIN
    RETURN CONVERT(datetime, community.LocalNow());
END');

IF COL_LENGTH(N'community.Events', N'Moniker') IS NULL
    ALTER TABLE community.Events ADD Moniker nvarchar(80) NULL;
IF COL_LENGTH(N'community.Events', N'IsCurrent') IS NULL
    ALTER TABLE community.Events ADD IsCurrent bit NOT NULL CONSTRAINT DF_Events_IsCurrent DEFAULT (0);
IF COL_LENGTH(N'community.Events', N'IsPublished') IS NULL
    ALTER TABLE community.Events ADD IsPublished bit NOT NULL CONSTRAINT DF_Events_IsPublished DEFAULT (1);
IF COL_LENGTH(N'community.Events', N'IsCancelled') IS NULL
    ALTER TABLE community.Events ADD IsCancelled bit NOT NULL CONSTRAINT DF_Events_IsCancelled DEFAULT (0);
IF COL_LENGTH(N'community.Events', N'IsLocked') IS NULL
    ALTER TABLE community.Events ADD IsLocked bit NOT NULL CONSTRAINT DF_Events_IsLocked DEFAULT (0);
IF COL_LENGTH(N'community.Events', N'TemplateKey') IS NOT NULL
BEGIN
    DECLARE @templateDefault sysname =
        (SELECT dc.name
         FROM sys.default_constraints dc
         INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
         WHERE dc.parent_object_id = OBJECT_ID(N'community.Events') AND c.name = N'TemplateKey');
    IF @templateDefault IS NOT NULL
    BEGIN
        DECLARE @dropDefault nvarchar(400) =
            N'ALTER TABLE community.Events DROP CONSTRAINT ' + QUOTENAME(@templateDefault);
        EXEC sys.sp_executesql @dropDefault;
    END;
    ALTER TABLE community.Events DROP COLUMN TemplateKey;
END;
IF COL_LENGTH(N'community.Events', N'CallForPapersOpenAt') IS NULL
    ALTER TABLE community.Events ADD CallForPapersOpenAt datetimeoffset NULL;
IF COL_LENGTH(N'community.Events', N'CallForPapersCloseAt') IS NULL
    ALTER TABLE community.Events ADD CallForPapersCloseAt datetimeoffset NULL;
IF COL_LENGTH(N'community.Events', N'CallForPapersUrl') IS NULL
    ALTER TABLE community.Events ADD CallForPapersUrl nvarchar(512) NOT NULL CONSTRAINT DF_Events_CfpUrl DEFAULT (N'');
IF COL_LENGTH(N'community.Events', N'Tagline') IS NOT NULL
BEGIN
    DECLARE @drop_Tagline nvarchar(400) = (
        SELECT N'ALTER TABLE community.Events DROP CONSTRAINT ' + QUOTENAME(dc.name)
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID(N'community.Events') AND c.name = N'Tagline');
    IF @drop_Tagline IS NOT NULL EXEC sys.sp_executesql @drop_Tagline;
    ALTER TABLE community.Events DROP COLUMN Tagline;
END;
IF COL_LENGTH(N'community.Events', N'Venue') IS NOT NULL
BEGIN
    DECLARE @drop_Venue nvarchar(400) = (
        SELECT N'ALTER TABLE community.Events DROP CONSTRAINT ' + QUOTENAME(dc.name)
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID(N'community.Events') AND c.name = N'Venue');
    IF @drop_Venue IS NOT NULL EXEC sys.sp_executesql @drop_Venue;
    ALTER TABLE community.Events DROP COLUMN Venue;
END;
IF COL_LENGTH(N'community.Events', N'HeroImageUrl') IS NOT NULL
BEGIN
    DECLARE @drop_HeroImageUrl nvarchar(400) = (
        SELECT N'ALTER TABLE community.Events DROP CONSTRAINT ' + QUOTENAME(dc.name)
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID(N'community.Events') AND c.name = N'HeroImageUrl');
    IF @drop_HeroImageUrl IS NOT NULL EXEC sys.sp_executesql @drop_HeroImageUrl;
    ALTER TABLE community.Events DROP COLUMN HeroImageUrl;
END;
IF COL_LENGTH(N'community.Events', N'TicketUrl') IS NOT NULL
BEGIN
    DECLARE @drop_TicketUrl nvarchar(400) = (
        SELECT N'ALTER TABLE community.Events DROP CONSTRAINT ' + QUOTENAME(dc.name)
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID(N'community.Events') AND c.name = N'TicketUrl');
    IF @drop_TicketUrl IS NOT NULL EXEC sys.sp_executesql @drop_TicketUrl;
    ALTER TABLE community.Events DROP COLUMN TicketUrl;
END;
IF COL_LENGTH(N'community.Events', N'TicketLabel') IS NOT NULL
BEGIN
    DECLARE @drop_TicketLabel nvarchar(400) = (
        SELECT N'ALTER TABLE community.Events DROP CONSTRAINT ' + QUOTENAME(dc.name)
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID(N'community.Events') AND c.name = N'TicketLabel');
    IF @drop_TicketLabel IS NOT NULL EXEC sys.sp_executesql @drop_TicketLabel;
    ALTER TABLE community.Events DROP COLUMN TicketLabel;
END;
UPDATE community.Events
SET Moniker = COALESCE(
    CASE WHEN LOWER(SessionizeMoniker) = N'1nn0vai2026' THEN N'1nn0vAI' END,
    NULLIF(LEFT(LTRIM(RTRIM(SessionizeMoniker)), 80), N''),
    CONCAT(N'evento-', ID))
WHERE Moniker IS NULL;

IF EXISTS (
    SELECT 1
    FROM community.Events
    GROUP BY Year, Moniker
    HAVING COUNT(*) > 1)
    THROW 50001, 'Duplicate (Year, Moniker) values must be resolved before applying this migration.', 1;

IF EXISTS (
    SELECT EMail
    FROM community.Users
    GROUP BY EMail
    HAVING COUNT(*) > 1)
    THROW 50002, 'Duplicate user emails must be resolved before enabling email PIN login.', 1;
IF EXISTS (
    SELECT LOWER(LTRIM(RTRIM(EMail)))
    FROM community.Users
    GROUP BY LOWER(LTRIM(RTRIM(EMail)))
    HAVING COUNT(*) > 1)
    THROW 50003, 'Duplicate normalized user emails must be resolved before enabling email PIN login.', 1;
IF EXISTS (
    SELECT 1
    FROM community.Users
    WHERE EMail IS NULL OR LTRIM(RTRIM(EMail)) = N'')
    THROW 50005, 'Users without an email address must be resolved before enabling email PIN login.', 1;

UPDATE community.Users
SET EMail = LOWER(LTRIM(RTRIM(EMail)))
WHERE EMail <> LOWER(LTRIM(RTRIM(EMail)));

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'community.Events') AND name = N'Moniker' AND is_nullable = 1)
    ALTER TABLE community.Events ALTER COLUMN Moniker nvarchar(80) NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.Events') AND name = N'UX_Events_Year_Moniker')
    CREATE UNIQUE INDEX UX_Events_Year_Moniker ON community.Events(Year, Moniker);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.Events') AND name = N'UX_Events_Current')
    CREATE UNIQUE INDEX UX_Events_Current ON community.Events(IsCurrent) WHERE IsCurrent = 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'community.Events') AND name = N'Name' AND max_length < 256)
    ALTER TABLE community.Events ALTER COLUMN Name nvarchar(128) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'community.Events') AND name = N'Description' AND max_length <> -1)
    ALTER TABLE community.Events ALTER COLUMN Description nvarchar(max) NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.Users') AND name = N'UX_Users_EMail')
    CREATE UNIQUE INDEX UX_Users_EMail ON community.Users(EMail);

IF COL_LENGTH(N'community.UserEventRegistration', N'TicketTokenHash') IS NULL
    ALTER TABLE community.UserEventRegistration ADD TicketTokenHash binary(32) NULL;
IF COL_LENGTH(N'community.UserEventRegistration', N'CheckedInAt') IS NULL
    ALTER TABLE community.UserEventRegistration ADD CheckedInAt datetimeoffset NULL;
IF EXISTS (
    SELECT 1
    FROM community.UserEventRegistration
    WHERE Status <> N'Cancelled'
    GROUP BY UserID, EventID
    HAVING COUNT(*) > 1)
    THROW 50004, 'Duplicate active user registrations must be resolved before enabling ticket registration.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.UserEventRegistration') AND name = N'UX_UserEventRegistration_User_Event')
    CREATE UNIQUE INDEX UX_UserEventRegistration_User_Event
        ON community.UserEventRegistration(UserID, EventID)
        WHERE Status <> N'Cancelled';

IF OBJECT_ID(N'community.UserConsentHistory', N'U') IS NULL
BEGIN
    CREATE TABLE community.UserConsentHistory
    (
        ID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_UserConsentHistory PRIMARY KEY,
        UserID int NOT NULL,
        ConsentType nvarchar(50) NOT NULL,
        IsGranted bit NOT NULL,
        PolicyVersion nvarchar(50) NOT NULL,
        ChangedAt datetimeoffset NOT NULL CONSTRAINT DF_UserConsentHistory_ChangedAt DEFAULT (community.LocalNow()),
        Source nvarchar(50) NOT NULL,
        CONSTRAINT FK_UserConsentHistory_Users FOREIGN KEY (UserID) REFERENCES community.Users(ID)
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.UserConsentHistory') AND name = N'IX_UserConsentHistory_User_Type_ChangedAt')
    CREATE INDEX IX_UserConsentHistory_User_Type_ChangedAt
        ON community.UserConsentHistory(UserID, ConsentType, ChangedAt DESC);
IF OBJECT_ID(N'community.EmailPinChallenges', N'U') IS NULL
BEGIN
    CREATE TABLE community.EmailPinChallenges
    (
        ID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmailPinChallenges PRIMARY KEY,
        UserID int NOT NULL,
        PinHash binary(32) NOT NULL,
        FirstName nvarchar(64) NOT NULL,
        LastName nvarchar(64) NOT NULL,
        AllowEventNotifications bit NOT NULL,
        AttemptCount tinyint NOT NULL CONSTRAINT DF_EmailPinChallenges_Attempts DEFAULT (0),
        CreatedAt datetimeoffset NOT NULL CONSTRAINT DF_EmailPinChallenges_CreatedAt DEFAULT (community.LocalNow()),
        ConsumedAt datetimeoffset NULL,
        CONSTRAINT FK_EmailPinChallenges_Users FOREIGN KEY (UserID) REFERENCES community.Users(ID)
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.EmailPinChallenges') AND name = N'IX_EmailPinChallenges_User_Active')
    CREATE INDEX IX_EmailPinChallenges_User_Active
        ON community.EmailPinChallenges(UserID, ConsumedAt, CreatedAt DESC);

IF OBJECT_ID(N'community.SpeakerEmailPinChallenges', N'U') IS NULL
BEGIN
    CREATE TABLE community.SpeakerEmailPinChallenges
    (
        ID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SpeakerEmailPinChallenges PRIMARY KEY,
        SpeakerID int NOT NULL,
        PinHash binary(32) NOT NULL,
        AttemptCount tinyint NOT NULL CONSTRAINT DF_SpeakerEmailPinChallenges_Attempts DEFAULT (0),
        CreatedAt datetimeoffset NOT NULL CONSTRAINT DF_SpeakerEmailPinChallenges_CreatedAt DEFAULT (community.LocalNow()),
        ConsumedAt datetimeoffset NULL,
        CONSTRAINT FK_SpeakerEmailPinChallenges_Speakers
            FOREIGN KEY (SpeakerID) REFERENCES community.Speakers(ID)
    );
END;
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'community.SpeakerEmailPinChallenges')
      AND name = N'IX_SpeakerEmailPinChallenges_Speaker_Active')
    CREATE INDEX IX_SpeakerEmailPinChallenges_Speaker_Active
        ON community.SpeakerEmailPinChallenges(SpeakerID, ConsumedAt, CreatedAt DESC);

IF OBJECT_ID(N'community.Sponsors', N'U') IS NULL
BEGIN
    CREATE TABLE community.Sponsors
    (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Sponsors PRIMARY KEY,
        SponsorKey nvarchar(80) NOT NULL,
        Name nvarchar(128) NOT NULL,
        Url nvarchar(512) NOT NULL,
        Tier nvarchar(50) NOT NULL,
        MediaAssetID int NULL,
        CONSTRAINT UX_Sponsors_SponsorKey UNIQUE (SponsorKey)
    );
END;

IF OBJECT_ID(N'community.MediaAssets', N'U') IS NULL
BEGIN
    CREATE TABLE community.MediaAssets
    (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MediaAssets PRIMARY KEY,
        FileName nvarchar(255) NOT NULL,
        ContentType nvarchar(100) NOT NULL,
        Content varbinary(max) NOT NULL,
        AltText nvarchar(256) NOT NULL CONSTRAINT DF_MediaAssets_AltText DEFAULT (N''),
        CreatedAt datetimeoffset NOT NULL CONSTRAINT DF_MediaAssets_CreatedAt DEFAULT (community.LocalNow())
    );
END;
IF COL_LENGTH(N'community.Sponsors', N'MediaAssetID') IS NOT NULL
   AND OBJECT_ID(N'community.MediaAssets', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Sponsors_MediaAssets')
    ALTER TABLE community.Sponsors ADD CONSTRAINT FK_Sponsors_MediaAssets
        FOREIGN KEY (MediaAssetID) REFERENCES community.MediaAssets(ID);

IF OBJECT_ID(N'community.EventSponsors', N'U') IS NULL
BEGIN
    CREATE TABLE community.EventSponsors
    (
        EventID int NOT NULL,
        SponsorID int NOT NULL,
        SortOrder int NOT NULL CONSTRAINT DF_EventSponsors_SortOrder DEFAULT (0),
        CONSTRAINT PK_EventSponsors PRIMARY KEY (EventID, SponsorID),
        CONSTRAINT FK_EventSponsors_Events FOREIGN KEY (EventID) REFERENCES community.Events(ID),
        CONSTRAINT FK_EventSponsors_Sponsors FOREIGN KEY (SponsorID) REFERENCES community.Sponsors(ID)
    );
END;
IF OBJECT_ID(N'community.EventSessions', N'U') IS NULL
BEGIN
    CREATE TABLE community.EventSessions
    (
        ID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_EventSessions PRIMARY KEY,
        EventID int NOT NULL,
        SessionizeSessionID nvarchar(80) NOT NULL,
        Title nvarchar(512) NOT NULL,
        Description nvarchar(max) NULL,
        StartsAt datetimeoffset NOT NULL,
        EndsAt datetimeoffset NOT NULL,
        Room nvarchar(128) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_EventSessions_IsActive DEFAULT (1),
        LastSyncedAt datetimeoffset NOT NULL CONSTRAINT DF_EventSessions_LastSyncedAt DEFAULT (community.LocalNow()),
        CONSTRAINT FK_EventSessions_Events FOREIGN KEY (EventID) REFERENCES community.Events(ID),
        CONSTRAINT UX_EventSessions_Event_Sessionize UNIQUE (EventID, SessionizeSessionID)
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.EventSessions') AND name = N'IX_EventSessions_Event_Active_Time')
    CREATE INDEX IX_EventSessions_Event_Active_Time
        ON community.EventSessions(EventID, IsActive, StartsAt, Room);

IF COL_LENGTH(N'community.EventSessions', N'VideoUrl') IS NULL
    ALTER TABLE community.EventSessions ADD VideoUrl nvarchar(512) NOT NULL CONSTRAINT DF_EventSessions_VideoUrl DEFAULT (N'');
IF COL_LENGTH(N'community.EventSessions', N'PresentationUrl') IS NULL
    ALTER TABLE community.EventSessions ADD PresentationUrl nvarchar(512) NOT NULL CONSTRAINT DF_EventSessions_PresentationUrl DEFAULT (N'');
IF COL_LENGTH(N'community.EventSessions', N'RepositoryUrl') IS NULL
    ALTER TABLE community.EventSessions ADD RepositoryUrl nvarchar(512) NOT NULL CONSTRAINT DF_EventSessions_RepositoryUrl DEFAULT (N'');

IF OBJECT_ID(N'community.EventSpeakers', N'U') IS NULL
BEGIN
    CREATE TABLE community.EventSpeakers
    (
        ID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_EventSpeakers PRIMARY KEY,
        EventID int NOT NULL,
        SessionizeSpeakerID nvarchar(80) NOT NULL,
        SpeakerID int NULL,
        Name nvarchar(256) NOT NULL,
        FirstName nvarchar(128) NOT NULL CONSTRAINT DF_EventSpeakers_FirstName DEFAULT (N''),
        LastName nvarchar(128) NOT NULL CONSTRAINT DF_EventSpeakers_LastName DEFAULT (N''),
        Bio nvarchar(max) NOT NULL CONSTRAINT DF_EventSpeakers_Bio DEFAULT (N''),
        TagLine nvarchar(512) NOT NULL CONSTRAINT DF_EventSpeakers_TagLine DEFAULT (N''),
        ProfilePictureUrl nvarchar(2048) NOT NULL CONSTRAINT DF_EventSpeakers_ProfilePictureUrl DEFAULT (N''),
        LinkedInUrl nvarchar(2048) NOT NULL CONSTRAINT DF_EventSpeakers_LinkedInUrl DEFAULT (N''),
        LastSyncedAt datetimeoffset NOT NULL CONSTRAINT DF_EventSpeakers_LastSyncedAt DEFAULT (community.LocalNow()),
        CONSTRAINT FK_EventSpeakers_Events FOREIGN KEY (EventID) REFERENCES community.Events(ID),
        CONSTRAINT FK_EventSpeakers_Speakers FOREIGN KEY (SpeakerID) REFERENCES community.Speakers(ID),
        CONSTRAINT UX_EventSpeakers_Event_External UNIQUE (EventID, SessionizeSpeakerID)
    );
END;

IF COL_LENGTH(N'community.EventSpeakers', N'FirstName') IS NULL
    ALTER TABLE community.EventSpeakers ADD FirstName nvarchar(128) NOT NULL CONSTRAINT DF_EventSpeakers_FirstName DEFAULT (N'');
IF COL_LENGTH(N'community.EventSpeakers', N'LastName') IS NULL
    ALTER TABLE community.EventSpeakers ADD LastName nvarchar(128) NOT NULL CONSTRAINT DF_EventSpeakers_LastName DEFAULT (N'');
IF COL_LENGTH(N'community.EventSpeakers', N'Bio') IS NULL
    ALTER TABLE community.EventSpeakers ADD Bio nvarchar(max) NOT NULL CONSTRAINT DF_EventSpeakers_Bio DEFAULT (N'');
IF COL_LENGTH(N'community.EventSpeakers', N'TagLine') IS NULL
    ALTER TABLE community.EventSpeakers ADD TagLine nvarchar(512) NOT NULL CONSTRAINT DF_EventSpeakers_TagLine DEFAULT (N'');
IF COL_LENGTH(N'community.EventSpeakers', N'ProfilePictureUrl') IS NULL
    ALTER TABLE community.EventSpeakers ADD ProfilePictureUrl nvarchar(2048) NOT NULL CONSTRAINT DF_EventSpeakers_ProfilePictureUrl DEFAULT (N'');
IF COL_LENGTH(N'community.EventSpeakers', N'LinkedInUrl') IS NULL
    ALTER TABLE community.EventSpeakers ADD LinkedInUrl nvarchar(2048) NOT NULL CONSTRAINT DF_EventSpeakers_LinkedInUrl DEFAULT (N'');
IF COL_LENGTH(N'community.EventSpeakers', N'SessionizeUrl') IS NULL
    ALTER TABLE community.EventSpeakers ADD SessionizeUrl nvarchar(2048) NOT NULL CONSTRAINT DF_EventSpeakers_SessionizeUrl DEFAULT (N'');
IF COL_LENGTH(N'community.EventSpeakers', N'SessionizeMoniker') IS NULL
    ALTER TABLE community.EventSpeakers ADD SessionizeMoniker nvarchar(256) NOT NULL CONSTRAINT DF_EventSpeakers_SessionizeMoniker DEFAULT (N'');
IF OBJECT_ID(N'community.EventSessionSpeakers', N'U') IS NULL
BEGIN
    CREATE TABLE community.EventSessionSpeakers
    (
        EventID int NOT NULL,
        SessionizeSessionID nvarchar(80) NOT NULL,
        EventSpeakerID bigint NOT NULL,
        CONSTRAINT PK_EventSessionSpeakers PRIMARY KEY (EventID, SessionizeSessionID, EventSpeakerID),
        CONSTRAINT FK_EventSessionSpeakers_EventSession FOREIGN KEY (EventID, SessionizeSessionID)
            REFERENCES community.EventSessions(EventID, SessionizeSessionID),
        CONSTRAINT FK_EventSessionSpeakers_EventSpeaker FOREIGN KEY (EventSpeakerID)
            REFERENCES community.EventSpeakers(ID)
    );
END;

IF OBJECT_ID(N'community.EventSpeakers', N'U') IS NULL
    THROW 50010, 'Required table community.EventSpeakers could not be created by the global setup script.', 1;

IF COL_LENGTH(N'community.EventSpeakers', N'FirstName') IS NULL
    ALTER TABLE community.EventSpeakers ADD FirstName nvarchar(128) NOT NULL CONSTRAINT DF_EventSpeakers_FirstName DEFAULT (N'');
IF COL_LENGTH(N'community.EventSpeakers', N'LastName') IS NULL
    ALTER TABLE community.EventSpeakers ADD LastName nvarchar(128) NOT NULL CONSTRAINT DF_EventSpeakers_LastName DEFAULT (N'');
IF COL_LENGTH(N'community.EventSpeakers', N'Bio') IS NULL
    ALTER TABLE community.EventSpeakers ADD Bio nvarchar(max) NOT NULL CONSTRAINT DF_EventSpeakers_Bio DEFAULT (N'');
IF COL_LENGTH(N'community.EventSpeakers', N'TagLine') IS NULL
    ALTER TABLE community.EventSpeakers ADD TagLine nvarchar(512) NOT NULL CONSTRAINT DF_EventSpeakers_TagLine DEFAULT (N'');
IF COL_LENGTH(N'community.EventSpeakers', N'ProfilePictureUrl') IS NULL
    ALTER TABLE community.EventSpeakers ADD ProfilePictureUrl nvarchar(2048) NOT NULL CONSTRAINT DF_EventSpeakers_ProfilePictureUrl DEFAULT (N'');
IF COL_LENGTH(N'community.EventSpeakers', N'LinkedInUrl') IS NULL
    ALTER TABLE community.EventSpeakers ADD LinkedInUrl nvarchar(2048) NOT NULL CONSTRAINT DF_EventSpeakers_LinkedInUrl DEFAULT (N'');

-- Alberghi convenzionati e dati della cena speaker (per speaker e per evento).
IF OBJECT_ID(N'community.Alberghi', N'U') IS NULL
BEGIN
    CREATE TABLE community.Alberghi
    (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Alberghi PRIMARY KEY,
        Nome nvarchar(128) NOT NULL,
        Url nvarchar(512) NOT NULL CONSTRAINT DF_Alberghi_Url DEFAULT (N''),
        CONSTRAINT UX_Alberghi_Nome UNIQUE (Nome)
    );
END;

INSERT INTO community.Alberghi (Nome, Url)
SELECT v.Nome, v.Url
FROM (VALUES
    (N'Hotel Santin', N'https://www.hotelsantin.it/'),
    (N'Hotel Minerva', N'https://www.booking.com/hotel/it/minerva-pordenone.en.html'),
    (N'Park Hotel Pordenone', N'https://www.parkhotelpordenone.it/')
) AS v(Nome, Url)
WHERE NOT EXISTS (SELECT 1 FROM community.Alberghi a WHERE a.Nome = v.Nome);

IF COL_LENGTH(N'community.EventSpeakers', N'PartecipaCenaSpeaker') IS NULL
    ALTER TABLE community.EventSpeakers ADD PartecipaCenaSpeaker bit NOT NULL
        CONSTRAINT DF_EventSpeakers_PartecipaCenaSpeaker DEFAULT (0);
IF COL_LENGTH(N'community.EventSpeakers', N'PartecipantiAggiuntiviCenaSpeaker') IS NULL
    ALTER TABLE community.EventSpeakers ADD PartecipantiAggiuntiviCenaSpeaker int NOT NULL
        CONSTRAINT DF_EventSpeakers_PartecipantiAggiuntiviCenaSpeaker DEFAULT (0);
IF COL_LENGTH(N'community.EventSpeakers', N'AlbergoID') IS NULL
    ALTER TABLE community.EventSpeakers ADD AlbergoID int NULL
        CONSTRAINT FK_EventSpeakers_Alberghi REFERENCES community.Alberghi(ID);

-- Cancellazione dell'account utente: flag logico (l'utente che ha già partecipato resta in anagrafica, anonimizzato).
IF COL_LENGTH(N'community.Users', N'IsDeleted') IS NULL
    ALTER TABLE community.Users ADD IsDeleted bit NOT NULL CONSTRAINT DF_Users_IsDeleted DEFAULT (0);
IF COL_LENGTH(N'community.Users', N'DeletedAt') IS NULL
    ALTER TABLE community.Users ADD DeletedAt datetimeoffset NULL;

-- Descrizione modificata dallo speaker: la sincronizzazione Sessionize non la sovrascrive.
IF COL_LENGTH(N'community.EventSessions', N'SpeakerDescription') IS NULL
    ALTER TABLE community.EventSessions ADD SpeakerDescription nvarchar(max) NULL;

-- Template PowerPoint dell'evento (file in MediaAssets, non pubblico).
IF COL_LENGTH(N'community.Events', N'SlideTemplateAssetID') IS NULL
    ALTER TABLE community.Events ADD SlideTemplateAssetID int NULL
        CONSTRAINT FK_Events_SlideTemplateAsset REFERENCES community.MediaAssets(ID);

-- Numero di cellulare dello speaker.
IF COL_LENGTH(N'community.Speakers', N'Cellulare') IS NULL
    ALTER TABLE community.Speakers ADD Cellulare nvarchar(32) NULL;

-- Speaker Id Sessionize sull'account speaker e storico delle modifiche (email, Speaker Id).
IF COL_LENGTH(N'community.Speakers', N'SessionizeSpeakerID') IS NULL
    ALTER TABLE community.Speakers ADD SessionizeSpeakerID nvarchar(80) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.Speakers') AND name = N'UX_Speakers_SessionizeSpeakerID')
    EXEC(N'CREATE UNIQUE INDEX UX_Speakers_SessionizeSpeakerID ON community.Speakers(SessionizeSpeakerID) WHERE SessionizeSpeakerID IS NOT NULL');

IF OBJECT_ID(N'community.SpeakerChangeLog', N'U') IS NULL
BEGIN
    CREATE TABLE community.SpeakerChangeLog
    (
        ID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SpeakerChangeLog PRIMARY KEY,
        SpeakerID int NOT NULL,
        Field nvarchar(64) NOT NULL,
        OldValue nvarchar(256) NULL,
        NewValue nvarchar(256) NULL,
        Source nvarchar(64) NOT NULL,
        ChangedBy nvarchar(128) NULL,
        ChangedAt datetimeoffset NOT NULL CONSTRAINT DF_SpeakerChangeLog_ChangedAt DEFAULT (community.LocalNow()),
        CONSTRAINT FK_SpeakerChangeLog_Speakers FOREIGN KEY (SpeakerID) REFERENCES community.Speakers(ID)
    );
END;

IF OBJECT_ID(N'community.Events', N'U') IS NULL
   OR OBJECT_ID(N'community.Users', N'U') IS NULL
   OR OBJECT_ID(N'community.Speakers', N'U') IS NULL
   OR OBJECT_ID(N'community.UserEventRegistration', N'U') IS NULL
    THROW 50020, 'The required legacy base tables are missing; global setup cannot continue.', 1;

IF COL_LENGTH(N'community.Events', N'CheckInToken') IS NULL
    ALTER TABLE community.Events ADD CheckInToken uniqueidentifier NOT NULL
        CONSTRAINT DF_Events_CheckInToken DEFAULT (NEWID());
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.Events') AND name = N'UX_Events_CheckInToken')
    CREATE UNIQUE INDEX UX_Events_CheckInToken ON community.Events(CheckInToken);

IF COL_LENGTH(N'community.Users', N'ToBeConfirmed') IS NULL
    ALTER TABLE community.Users ADD ToBeConfirmed bit NOT NULL
        CONSTRAINT DF_Users_ToBeConfirmed DEFAULT (0);
IF COL_LENGTH(N'community.Speakers', N'IsSuperUser') IS NULL
    ALTER TABLE community.Speakers ADD IsSuperUser bit NOT NULL
        CONSTRAINT DF_Speakers_IsSuperUser DEFAULT (0);
IF COL_LENGTH(N'community.Speakers', N'CanCheckIn') IS NULL
    ALTER TABLE community.Speakers ADD CanCheckIn bit NOT NULL
        CONSTRAINT DF_Speakers_CanCheckIn DEFAULT (0);
IF COL_LENGTH(N'community.UserEventRegistration', N'IsEatingPizza') IS NULL
    ALTER TABLE community.UserEventRegistration ADD IsEatingPizza bit NOT NULL
        CONSTRAINT DF_UserEventRegistration_IsEatingPizza DEFAULT (0);
IF COL_LENGTH(N'community.UserEventRegistration', N'LactoseIntolerant') IS NULL
    ALTER TABLE community.UserEventRegistration ADD LactoseIntolerant bit NOT NULL
        CONSTRAINT DF_UserEventRegistration_LactoseIntolerant DEFAULT (0);

IF OBJECT_ID(N'community.EventRegistrationConfirmations', N'U') IS NULL
BEGIN
    CREATE TABLE community.EventRegistrationConfirmations
    (
        ID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_EventRegistrationConfirmations PRIMARY KEY,
        RegistrationID int NOT NULL,
        UserID int NOT NULL,
        EventID int NOT NULL,
        TokenHash binary(32) NOT NULL,
        Purpose nvarchar(16) NOT NULL,
        CreatedAt datetimeoffset NOT NULL CONSTRAINT DF_EventRegistrationConfirmations_CreatedAt DEFAULT (community.LocalNow()),
        ExpiresAt datetimeoffset NOT NULL,
        ConsumedAt datetimeoffset NULL,
        CONSTRAINT FK_EventRegistrationConfirmations_Registration FOREIGN KEY (RegistrationID)
            REFERENCES community.UserEventRegistration(ID),
        CONSTRAINT FK_EventRegistrationConfirmations_User FOREIGN KEY (UserID)
            REFERENCES community.Users(ID),
        CONSTRAINT FK_EventRegistrationConfirmations_Event FOREIGN KEY (EventID)
            REFERENCES community.Events(ID),
        CONSTRAINT CK_EventRegistrationConfirmations_Purpose CHECK (Purpose IN (N'Registration', N'CheckIn'))
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.EventRegistrationConfirmations') AND name = N'IX_EventRegistrationConfirmations_Token_Active')
    CREATE INDEX IX_EventRegistrationConfirmations_Token_Active
        ON community.EventRegistrationConfirmations(TokenHash, ExpiresAt)
        WHERE ConsumedAt IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'community.EventRegistrationConfirmations') AND name = N'IX_EventRegistrationConfirmations_Registration_Active')
    CREATE INDEX IX_EventRegistrationConfirmations_Registration_Active
        ON community.EventRegistrationConfirmations(RegistrationID, ConsumedAt);
-- Date create prima di questa versione: i valori di default passano all'ora locale e le date in UTC (+00:00)
-- vengono espresse con l'offset locale (stesso istante, stesso valore assoluto). Ripetibile.
DECLARE @localDefaults nvarchar(max) = N'';
SELECT @localDefaults += N'ALTER TABLE community.' + QUOTENAME(t.name) + N' DROP CONSTRAINT ' + QUOTENAME(dc.name) + N'; '
    + N'ALTER TABLE community.' + QUOTENAME(t.name) + N' ADD CONSTRAINT ' + QUOTENAME(dc.name) + N' DEFAULT ('
    + CASE WHEN ty.name = N'datetime' THEN N'community.LocalNowDateTime()' ELSE N'community.LocalNow()' END
    + N') FOR ' + QUOTENAME(c.name) + N'; '
FROM sys.default_constraints dc
INNER JOIN sys.tables t ON t.object_id = dc.parent_object_id
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id AND s.name = N'community'
INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE dc.definition LIKE N'%sysutcdatetime%' OR dc.definition LIKE N'%getdate()%' OR dc.definition LIKE N'%getutcdate%';
IF @localDefaults <> N'' EXEC sys.sp_executesql @localDefaults;

DECLARE @localZone nvarchar(128) = COALESCE((SELECT [Value] FROM community.Settings WHERE [Key] = N'TimeZone'), N'W. Europe Standard Time');
DECLARE @localDates nvarchar(max) = N'';
SELECT @localDates += N'UPDATE community.' + QUOTENAME(v.tbl) + N' SET ' + QUOTENAME(v.col) + N' = ' + QUOTENAME(v.col)
    + N' AT TIME ZONE @zone WHERE ' + QUOTENAME(v.col) + N' IS NOT NULL AND DATEPART(TZoffset, ' + QUOTENAME(v.col) + N') = 0; '
FROM (VALUES
    (N'EmailPinChallenges', N'CreatedAt'), (N'EmailPinChallenges', N'ConsumedAt'),
    (N'EventRegistrationConfirmations', N'CreatedAt'), (N'EventRegistrationConfirmations', N'ExpiresAt'), (N'EventRegistrationConfirmations', N'ConsumedAt'),
    (N'EventSessions', N'LastSyncedAt'), (N'EventSpeakers', N'LastSyncedAt'),
    (N'MediaAssets', N'CreatedAt'), (N'SpeakerChangeLog', N'ChangedAt'),
    (N'SpeakerEmailPinChallenges', N'CreatedAt'), (N'SpeakerEmailPinChallenges', N'ConsumedAt'),
    (N'UserConsentHistory', N'ChangedAt'), (N'UserEventRegistration', N'CheckedInAt'),
    (N'UserLogs', N'Timstamp'), (N'Users', N'DeletedAt')
) AS v(tbl, col)
WHERE COL_LENGTH(N'community.' + v.tbl, v.col) IS NOT NULL;
IF @localDates <> N'' EXEC sys.sp_executesql @localDates, N'@zone nvarchar(128)', @localZone;

    -- First SuperUser (created only if missing; nothing else is touched).
    IF NOT EXISTS (SELECT 1 FROM community.Users WHERE LOWER(LTRIM(RTRIM(EMail))) = N'marco.parenzan@1nn0va.it')
        INSERT INTO community.Users
            (FirstName, LastName, EMail, LastPin, AllowEventsNotifications, RequestCancellation, ToBeConfirmed)
        VALUES (N'Marco', N'Parenzan', N'marco.parenzan@1nn0va.it', NULL, 0, 0, 0);

    IF NOT EXISTS (SELECT 1 FROM community.Speakers WHERE LOWER(LTRIM(RTRIM(EMail))) = N'marco.parenzan@1nn0va.it')
        INSERT INTO community.Speakers
            (FirstName, LastName, EMail, LastPin, SessionizeName, IsSuperUser, CanCheckIn)
        VALUES (N'Marco', N'Parenzan', N'marco.parenzan@1nn0va.it', N'', N'Marco Parenzan', 1, 1);
    ELSE
        UPDATE community.Speakers SET IsSuperUser = 1, CanCheckIn = 1
        WHERE LOWER(LTRIM(RTRIM(EMail))) = N'marco.parenzan@1nn0va.it';

    COMMIT TRANSACTION;
    PRINT N'Provisioning completed.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

GO

-- ------------------------------------------------------------------------------------------------------------
-- Final check: lists the objects the application needs that are still missing. No row = everything is in place.
-- ------------------------------------------------------------------------------------------------------------
SET NOCOUNT ON;

SELECT oggetto AS manca
FROM (VALUES
    -- tables
    (N'tabella community.Users',                      IIF(OBJECT_ID(N'community.Users', N'U') IS NULL, 0, 1)),
    (N'tabella community.Speakers',                   IIF(OBJECT_ID(N'community.Speakers', N'U') IS NULL, 0, 1)),
    (N'tabella community.Events',                     IIF(OBJECT_ID(N'community.Events', N'U') IS NULL, 0, 1)),
    (N'tabella community.UserEventRegistration',      IIF(OBJECT_ID(N'community.UserEventRegistration', N'U') IS NULL, 0, 1)),
    (N'tabella community.EventSessions',              IIF(OBJECT_ID(N'community.EventSessions', N'U') IS NULL, 0, 1)),
    (N'tabella community.EventSpeakers',              IIF(OBJECT_ID(N'community.EventSpeakers', N'U') IS NULL, 0, 1)),
    (N'tabella community.EventSessionSpeakers',       IIF(OBJECT_ID(N'community.EventSessionSpeakers', N'U') IS NULL, 0, 1)),
    (N'tabella community.Sponsors',                   IIF(OBJECT_ID(N'community.Sponsors', N'U') IS NULL, 0, 1)),
    (N'tabella community.EventSponsors',              IIF(OBJECT_ID(N'community.EventSponsors', N'U') IS NULL, 0, 1)),
    (N'tabella community.MediaAssets',                IIF(OBJECT_ID(N'community.MediaAssets', N'U') IS NULL, 0, 1)),
    (N'tabella community.UserConsentHistory',         IIF(OBJECT_ID(N'community.UserConsentHistory', N'U') IS NULL, 0, 1)),
    (N'tabella community.UserLogs',                   IIF(OBJECT_ID(N'community.UserLogs', N'U') IS NULL, 0, 1)),
    (N'tabella community.EmailPinChallenges',         IIF(OBJECT_ID(N'community.EmailPinChallenges', N'U') IS NULL, 0, 1)),
    (N'tabella community.SpeakerEmailPinChallenges',  IIF(OBJECT_ID(N'community.SpeakerEmailPinChallenges', N'U') IS NULL, 0, 1)),
    (N'tabella community.EventRegistrationConfirmations', IIF(OBJECT_ID(N'community.EventRegistrationConfirmations', N'U') IS NULL, 0, 1)),
    (N'tabella community.Alberghi',                   IIF(OBJECT_ID(N'community.Alberghi', N'U') IS NULL, 0, 1)),
    (N'tabella community.SpeakerChangeLog',           IIF(OBJECT_ID(N'community.SpeakerChangeLog', N'U') IS NULL, 0, 1)),
    (N'tabella community.Settings',                   IIF(OBJECT_ID(N'community.Settings', N'U') IS NULL, 0, 1)),
    (N'funzione community.LocalNow()',                IIF(OBJECT_ID(N'community.LocalNow', N'FN') IS NULL, 0, 1)),
    (N'funzione community.LocalNowDateTime()',        IIF(OBJECT_ID(N'community.LocalNowDateTime', N'FN') IS NULL, 0, 1)),
    -- columns added over time
    (N'colonna Users.IsDeleted',                      IIF(COL_LENGTH(N'community.Users', N'IsDeleted') IS NULL, 0, 1)),
    (N'colonna Users.DeletedAt',                      IIF(COL_LENGTH(N'community.Users', N'DeletedAt') IS NULL, 0, 1)),
    (N'colonna Speakers.Cellulare',                   IIF(COL_LENGTH(N'community.Speakers', N'Cellulare') IS NULL, 0, 1)),
    (N'colonna Speakers.SessionizeSpeakerID',         IIF(COL_LENGTH(N'community.Speakers', N'SessionizeSpeakerID') IS NULL, 0, 1)),
    (N'colonna Events.IsLocked',                      IIF(COL_LENGTH(N'community.Events', N'IsLocked') IS NULL, 0, 1)),
    (N'colonna Events.SlideTemplateAssetID',          IIF(COL_LENGTH(N'community.Events', N'SlideTemplateAssetID') IS NULL, 0, 1)),
    (N'colonna EventSessions.SpeakerDescription',     IIF(COL_LENGTH(N'community.EventSessions', N'SpeakerDescription') IS NULL, 0, 1)),
    (N'colonna EventSpeakers.PartecipaCenaSpeaker',   IIF(COL_LENGTH(N'community.EventSpeakers', N'PartecipaCenaSpeaker') IS NULL, 0, 1)),
    (N'colonna EventSpeakers.PartecipantiAggiuntiviCenaSpeaker', IIF(COL_LENGTH(N'community.EventSpeakers', N'PartecipantiAggiuntiviCenaSpeaker') IS NULL, 0, 1)),
    (N'colonna EventSpeakers.AlbergoID',              IIF(COL_LENGTH(N'community.EventSpeakers', N'AlbergoID') IS NULL, 0, 1))
) AS check_list(oggetto, presente)
WHERE presente = 0
ORDER BY oggetto;

IF @@ROWCOUNT = 0
    PRINT N'Verification completed: the schema is complete.';
ELSE
    PRINT N'WARNING: some objects are missing (see the list above).';

/*
    OPTIONAL, ONE TIME - registration dates written in UTC.

    UserEventRegistration.RegistrationDate is a plain "datetime" (no time zone). Before the application wrote local time,
    a server running in UTC (Azure) stored UTC wall-clock times there. The script cannot know which rows were written
    there: set @since to the moment the Azure site started registering people, then uncomment and run the block ONCE.

    DECLARE @since datetime = '2026-10-10 12:00';
    DECLARE @zone nvarchar(128) = COALESCE((SELECT [Value] FROM community.Settings WHERE [Key] = N'TimeZone'), N'W. Europe Standard Time');
    UPDATE community.UserEventRegistration
    SET RegistrationDate = CONVERT(datetime, (RegistrationDate AT TIME ZONE N'UTC') AT TIME ZONE @zone)
    WHERE RegistrationDate >= @since;
*/
