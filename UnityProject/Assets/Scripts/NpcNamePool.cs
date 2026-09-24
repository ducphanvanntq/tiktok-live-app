using System;
using System.Collections.Generic;

namespace TikTokLiveGame
{
    /// <summary>A shuffled name deck: no repeated NPC names until the pool is exhausted.</summary>
    internal sealed class NpcNamePool
    {
        private static readonly string[] FamilyNames =
        {
            "Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Huỳnh", "Phan", "Vũ",
            "Võ", "Đặng", "Bùi", "Đỗ", "Hồ", "Ngô", "Dương", "Lý",
            "Đinh", "Trịnh", "Mai", "Đào", "Lâm", "Cao", "Tạ", "Hà"
        };
        private static readonly string[] GivenNames =
        {
            "Minh Anh", "Ngọc Anh", "Bảo Anh", "Tuấn Anh", "Quỳnh Anh", "Lan Anh",
            "Phương Anh", "Đức Anh", "Hải Anh", "Hoàng Anh", "Gia Bảo", "Quốc Bảo",
            "Minh Châu", "Bảo Châu", "Ngọc Diệp", "Thùy Dung", "Hải Đăng", "Minh Đức",
            "Hương Giang", "Ngọc Hà", "Thanh Hà", "Thu Hà", "Gia Hân", "Ngọc Hân",
            "Quang Huy", "Gia Huy", "Khánh Huyền", "Minh Khang", "Bảo Khang", "Đăng Khoa",
            "Anh Khôi", "Tuấn Kiệt", "Khánh Linh", "Mai Linh", "Thảo Linh", "Gia Linh",
            "Bảo Long", "Hoàng Long", "Nhật Minh", "Quang Minh", "Hoài Nam", "Phương Nam",
            "Bảo Ngọc", "Hồng Ngọc", "Kim Ngân", "Yến Nhi", "Khánh Nhi", "Thanh Phong",
            "Minh Phúc", "Anh Quân", "Minh Quân", "Như Quỳnh", "Đức Thành", "Huyền Trang",
            "Bảo Trâm", "Thanh Trúc", "Anh Thư", "Thảo Vy", "Tường Vy", "Hoàng Yến"
        };
        private static readonly string[] Nicknames =
        {
            "Bắp Rang", "Cà Khịa", "Tí Tởn", "Tèo Teo", "Bảy Bóng", "Sáu Lắc", "Năm Rung", "Út Quẩy",
            "Cô Ba Lắc", "Chú Tư Nhún", "Anh Hai Quẩy", "Bé Hột Mít", "Mèo Mập", "Heo Hay Hờn", "Gà Mơ", "Vịt Lộn",
            "Cá Khô", "Mực Một Nắng", "Bánh Bao", "Bánh Bèo", "Bún Đậu", "Mắm Tôm", "Chả Lụa", "Trà Đá",
            "Sữa Đậu", "Khoai Lang", "Đậu Phộng", "Hạt Dưa", "Xoài Lắc", "Cóc Dầm", "Chanh Chua", "Ớt Hiểm",
            "Củ Cải", "Rau Răm", "Hành Phi", "Tỏi Bay", "Gừng Cay", "Muối Tiêu", "Nước Mắm", "Cơm Nguội",
            "Dép Tổ Ong", "Quần Hoa", "Áo Bông", "Tóc Dựng", "Răng Khểnh", "Má Bánh Bao", "Mắt Hí", "Bụng Bự",
            "Chân Ngắn", "Cổ Cao", "Đầu Nấm", "Mặt Ngầu", "Hay Dỗi", "Hay Cười", "Thích Quẩy", "Lười Nhảy",
            "Ngủ Gật", "Đi Trễ", "Quên Dép", "Mất Sóng", "Hết Pin", "Kẹt Xe", "Rớt Mạng", "Đang Ăn",
            "Chưa Tắm", "No Căng", "Say Nhẹ", "Khát Nước", "Lạc Trôi", "Quẩy Dở", "Nhún Sai", "Lắc Nhầm",
            "Cười Xỉu", "Tưng Tửng", "Ngơ Ngác", "Hơi Mệt", "Rất Ổn", "Không Sao", "Bình Tĩnh", "Vui Vẻ"
        };

        private static readonly string[] AllNames = BuildNames();
        private readonly Random random;
        private readonly string[] deck = new string[AllNames.Length];
        private int cursor = AllNames.Length;

        public static int Count => AllNames.Length;

        public NpcNamePool() : this(new Random()) { }
        internal NpcNamePool(int seed) : this(new Random(seed)) { }
        private NpcNamePool(Random source) => random = source;

        public string Next(ISet<string> occupied)
        {
            for (int attempt = 0; attempt < AllNames.Length; attempt++)
            {
                if (cursor == deck.Length)
                {
                    Array.Copy(AllNames, deck, deck.Length);
                    for (int i = deck.Length - 1; i > 0; i--)
                    {
                        int j = random.Next(i + 1);
                        (deck[i], deck[j]) = (deck[j], deck[i]);
                    }
                    cursor = 0;
                }
                string name = deck[cursor++];
                if (!occupied.Contains(name)) return name;
            }
            throw new InvalidOperationException("The NPC name pool has no available names.");
        }

        private static string[] BuildNames()
        {
            var names = new List<string>(Nicknames);
            foreach (string family in FamilyNames)
                foreach (string given in GivenNames) names.Add(family + " " + given);
            return names.ToArray();
        }

    }
}
