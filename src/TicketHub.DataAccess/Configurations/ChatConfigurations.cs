using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketHub.DataAccess.Entities;

namespace TicketHub.DataAccess.Configurations;

public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Title).IsRequired().HasMaxLength(150);

        builder.HasOne(c => c.Ticket)
               .WithOne(t => t.Conversation)
               .HasForeignKey<Conversation>(c => c.TicketId)
               .OnDelete(DeleteBehavior.Cascade);

        // A FILTERED UNIQUE INDEX — worth understanding, because the naive version is wrong.
        //
        // We want "at most one conversation per ticket". A plain unique index on a nullable
        // column would also mean "at most one row with TicketId = NULL" in SQL Server, which
        // would allow exactly one direct-message conversation in the entire system.
        //
        // The WHERE clause makes the uniqueness apply only to the rows that have a ticket.
        builder.HasIndex(c => c.TicketId)
               .IsUnique()
               .HasFilter("[TicketId] IS NOT NULL")
               .HasDatabaseName("IX_Conversations_TicketId");

        // The inbox ordering: most recent activity first. This is why Conversation keeps a
        // denormalised LastMessageAt instead of computing MAX(SentAt) per row.
        builder.HasIndex(c => c.LastMessageAt).HasDatabaseName("IX_Conversations_LastMessageAt");
    }
}

/// <summary>
/// The N:N join we wrote by hand, because it carries data.
/// </summary>
public class ConversationParticipantConfiguration : IEntityTypeConfiguration<ConversationParticipant>
{
    public void Configure(EntityTypeBuilder<ConversationParticipant> builder)
    {
        builder.ToTable("ConversationParticipants");

        builder.HasKey(p => p.Id);

        // One membership row per (conversation, user). Without this you can add the same
        // person to a conversation twice, and then every "unread count" is doubled and
        // nobody can work out why.
        builder.HasIndex(p => new { p.ConversationId, p.UserId })
               .IsUnique()
               .HasDatabaseName("IX_ConversationParticipants_Conversation_User");

        // "Which conversations am I in" — the query behind the inbox.
        builder.HasIndex(p => p.UserId).HasDatabaseName("IX_ConversationParticipants_UserId");

        builder.HasOne(p => p.Conversation)
               .WithMany(c => c.Participants)
               .HasForeignKey(p => p.ConversationId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.User)
               .WithMany(u => u.Conversations)
               .HasForeignKey(p => p.UserId)
               .OnDelete(DeleteBehavior.Cascade);

        // Required navigation to a soft-filtered entity — see the note on
        // TicketHistoryConfiguration for why this line has to be here.
        builder.HasQueryFilter(p => !p.Conversation.IsDeleted);
    }
}

public class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable("ChatMessages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Body).IsRequired().HasMaxLength(2000);
        builder.Property(m => m.SenderNameSnapshot).IsRequired().HasMaxLength(120);
        builder.Property(m => m.ClientMessageId).HasMaxLength(64);

        builder.HasOne(m => m.Conversation)
               .WithMany(c => c.Messages)
               .HasForeignKey(m => m.ConversationId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Sender)
               .WithMany()
               .HasForeignKey(m => m.SenderId)
               .OnDelete(DeleteBehavior.SetNull);

        // THE query a chat makes: "the newest N messages in this conversation", over and
        // over. Descending on Id because we page backwards through history with
        // "give me messages older than id N" — see MessageQuery.BeforeMessageId.
        builder.HasIndex(m => new { m.ConversationId, m.Id })
               .HasDatabaseName("IX_ChatMessages_Conversation_Id");

        // Makes a retried send idempotent: the same ClientMessageId in the same conversation
        // cannot be stored twice. Filtered, because the value is optional.
        builder.HasIndex(m => new { m.ConversationId, m.ClientMessageId })
               .IsUnique()
               .HasFilter("[ClientMessageId] IS NOT NULL")
               .HasDatabaseName("IX_ChatMessages_ClientMessageId");

        builder.HasQueryFilter(m => !m.IsDeleted && !m.Conversation.IsDeleted);
    }
}
