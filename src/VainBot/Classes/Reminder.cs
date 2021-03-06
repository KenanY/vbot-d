﻿using System;

namespace VainBot.Classes
{
    public class Reminder
    {
        public int Id { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset FireAt { get; set; }

        public long UserId { get; set; }

        public long ChannelId { get; set; }

        public long GuildId { get; set; }

        public bool IsDM { get; set; }

        public string Message { get; set; }
    }
}
