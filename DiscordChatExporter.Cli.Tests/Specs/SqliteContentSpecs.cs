using System.Threading.Tasks;
using DiscordChatExporter.Cli.Tests.Infra;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class SqliteContentSpecs
{
    [Fact]
    public async Task I_can_export_a_channel_to_a_queryable_sqlite_database()
    {
        // Act
        var dbPath = await ExportWrapper.ExportAsDbAsync(ChannelIds.DateRangeTestCases);

        // Assert
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = false,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString()
        );
        connection.Open();

        long messageCount;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM messages;";
            messageCount = (long)command.ExecuteScalar()!;
        }

        messageCount.Should().BeGreaterThan(0);

        // export_info.message_count agrees with the messages table.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT message_count FROM export_info;";
            ((long)command.ExecuteScalar()!).Should().Be(messageCount);
        }

        // The FTS index is populated 1:1 with messages.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM messages_fts;";
            ((long)command.ExecuteScalar()!).Should().Be(messageCount);
        }
    }
}
