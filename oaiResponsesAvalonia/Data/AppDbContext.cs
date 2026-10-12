using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using oaiResponsesAvalonia.Services;

namespace oaiResponsesAvalonia.Data
{
    public class AppDbContext : DbContext
    {
        public static string DatabaseFilePath { get; set; } =
            AppPaths.DatabaseFilePath;

        public DbSet<ChatSession> Sessions { get; set; }
        public DbSet<ChatMessage> Messages { get; set; }
        public DbSet<MediaFile> Media { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlite($"Data Source={DatabaseFilePath}");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Explicit table names for predictability
            modelBuilder.Entity<ChatSession>().ToTable("Sessions");
            modelBuilder.Entity<ChatMessage>().ToTable("Messages");
            modelBuilder.Entity<MediaFile>().ToTable("Media");

            // Session -> Messages
            modelBuilder.Entity<ChatSession>()
                .HasMany(s => s.Messages)
                .WithOne(m => m.ChatSession)
                .HasForeignKey(m => m.ChatSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Message -> MediaFiles
            modelBuilder.Entity<ChatMessage>()
                .HasMany(m => m.MediaFiles)
                .WithOne(mf => mf.ChatMessage)
                .HasForeignKey(mf => mf.ChatMessageId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes declared in model
            modelBuilder.Entity<ChatSession>()
                .HasIndex(s => s.LastUsedAt);

            modelBuilder.Entity<ChatSession>()
                .HasIndex(s => s.Endpoint);

            modelBuilder.Entity<ChatMessage>()
                .HasIndex(m => m.ChatSessionId);

            modelBuilder.Entity<ChatMessage>()
                .HasIndex(m => m.Timestamp);

            modelBuilder.Entity<MediaFile>()
                .HasIndex(mf => mf.ChatMessageId);
        }

        public static void InitializeDatabase()
        {
            AppPaths.EnsureDataDirectory();

            if (!File.Exists(DatabaseFilePath) && File.Exists(AppPaths.LegacyDatabaseFilePath))
            {
                File.Copy(AppPaths.LegacyDatabaseFilePath, DatabaseFilePath);

                foreach (string suffix in new[] { "-wal", "-shm" })
                {
                    string legacySidecar = AppPaths.LegacyDatabaseFilePath + suffix;
                    if (File.Exists(legacySidecar))
                    {
                        File.Copy(legacySidecar, DatabaseFilePath + suffix);
                    }
                }
            }

            string? folder = Path.GetDirectoryName(DatabaseFilePath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            using var db = new AppDbContext();

            // Keep this for now. Later we can move to Migrations.
            db.Database.EnsureCreated();

            // Ensure indexes exist even if DB was created before the model had them
            db.Database.ExecuteSqlRaw(
                "CREATE INDEX IF NOT EXISTS IX_Sessions_LastUsedAt ON Sessions (LastUsedAt);");

            db.Database.ExecuteSqlRaw(
                "CREATE INDEX IF NOT EXISTS IX_Sessions_Endpoint ON Sessions (Endpoint);");

            db.Database.ExecuteSqlRaw(
                "CREATE INDEX IF NOT EXISTS IX_Messages_ChatSessionId ON Messages (ChatSessionId);");

            db.Database.ExecuteSqlRaw(
                "CREATE INDEX IF NOT EXISTS IX_Messages_Timestamp ON Messages (Timestamp);");

            db.Database.ExecuteSqlRaw(
                "CREATE INDEX IF NOT EXISTS IX_Media_ChatMessageId ON Media (ChatMessageId);");
            if (!ColumnExists(db, "Messages", "ImageToolSettingsJson"))
            {
                db.Database.ExecuteSqlRaw(
                    "ALTER TABLE Messages ADD COLUMN ImageToolSettingsJson TEXT NOT NULL DEFAULT '';");
            }
            if (!ColumnExists(db, "Messages", "DeveloperToolSettingsJson"))
            {
                db.Database.ExecuteSqlRaw(
                    "ALTER TABLE Messages ADD COLUMN DeveloperToolSettingsJson TEXT NOT NULL DEFAULT '';");
            }
            if (!ColumnExists(db, "Messages", "ToolCallLogJson"))
            {
                db.Database.ExecuteSqlRaw(
                    "ALTER TABLE Messages ADD COLUMN ToolCallLogJson TEXT NOT NULL DEFAULT '';");
            }
        }
        private static bool ColumnExists(AppDbContext db, string tableName, string columnName)
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";

            db.Database.OpenConnection();
            try
            {
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string existingName = reader["name"]?.ToString() ?? string.Empty;
                    if (string.Equals(existingName, columnName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            finally
            {
                db.Database.CloseConnection();
            }

            return false;
        }
    }
}