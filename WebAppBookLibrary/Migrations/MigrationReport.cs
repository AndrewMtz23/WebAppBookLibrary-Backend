namespace WebAppBookLibrary.Migrations;

public sealed record MigrationReport(long Scanned, long AlreadyCurrent, long Transformable, long Anomalous, long Updated, IReadOnlyList<string> AnomalyCodes);

public enum MigrationDisposition { AlreadyCurrent, Transformable, Anomalous }

public sealed record BookMigrationPlan(MigrationDisposition Disposition, MongoDB.Bson.BsonDocument? Replacement, IReadOnlyList<string> AnomalyCodes);
