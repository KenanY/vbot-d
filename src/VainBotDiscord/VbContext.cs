﻿using Microsoft.EntityFrameworkCore;
using VainBotDiscord.Classes;

namespace VainBotDiscord
{
    public class VbContext : DbContext
    {
        public VbContext(DbContextOptions options)
            : base(options)
        {
        }

        public DbSet<KeyValue> KeyValues { get; set; }
        public DbSet<TwitchStreamToCheck> StreamsToCheck { get; set; }
        public DbSet<TwitchLiveStream> TwitchLiveStreams { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<KeyValue>(e =>
            {
                e.ToTable("key_value");
                e.HasKey(kv => kv.Key);

                e.Property(kv => kv.Key).HasColumnName("key").IsRequired().HasMaxLength(100);
                e.Property(kv => kv.Value).HasColumnName("value").IsRequired().HasMaxLength(250);
            });

            modelBuilder.Entity<TwitchStreamToCheck>(e =>
            {
                e.ToTable("twitch_stream_to_check");
                e.HasKey(t => t.Id);

                e.Property(t => t.Id).HasColumnName("id");
                e.Property(t => t.TwitchId).HasColumnName("twitch_id").IsRequired().HasMaxLength(50);
                e.Property(t => t.Username).HasColumnName("username").IsRequired().HasMaxLength(50);
                e.Property(t => t.MessageToPost).HasColumnName("message_to_post").IsRequired().HasMaxLength(1500);
                e.Property(t => t.ChannelId).HasColumnName("channel_id").IsRequired();
                e.Property(t => t.GuildId).HasColumnName("guild_id").IsRequired();
                e.Property(t => t.IsEmbedded).HasColumnName("is_embedded").IsRequired();
                e.Property(t => t.IsDeleted).HasColumnName("is_deleted").IsRequired();
                e.Property(t => t.CurrentMessageId).HasColumnName("current_message_id");
            });

            modelBuilder.Entity<TwitchLiveStream>(e =>
            {
                e.ToTable("twitch_live_stream");
                e.HasKey(t => t.TwitchStreamId);

                e.Property(t => t.StartedAt).HasColumnName("started_at").IsRequired();
                e.Property(t => t.FirstOfflineAt).HasColumnName("first_offline_at");
                e.Property(t => t.TwitchStreamId).HasColumnName("twitch_stream_id").IsRequired().HasMaxLength(50);
                e.Property(t => t.TwitchUserId).HasColumnName("twitch_user_id").IsRequired().HasMaxLength(50);
                e.Property(t => t.TwitchLogin).HasColumnName("twitch_login").IsRequired().HasMaxLength(50);
                e.Property(t => t.TwitchDisplayName).HasColumnName("twitch_display_name").IsRequired().HasMaxLength(50);
                e.Property(t => t.ViewerCount).HasColumnName("viewer_count").IsRequired();
                e.Property(t => t.Title).HasColumnName("title").IsRequired().HasMaxLength(200);
                e.Property(t => t.GameName).HasColumnName("game_name").IsRequired().HasMaxLength(300);
                e.Property(t => t.GameId).HasColumnName("game_id").IsRequired().HasMaxLength(50);
                e.Property(t => t.ThumbnailUrl).HasColumnName("thumbnail_url").IsRequired().HasMaxLength(350);
                e.Property(t => t.ProfileImageUrl).HasColumnName("profile_image_url").IsRequired().HasMaxLength(350);
            });
        }
    }
}
