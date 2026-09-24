# Kiểm tra server Rust — 2026-09-20

Phạm vi: mã nguồn trong `server/`, transport TikTok được vendor và giao thức với
game/bảng điều khiển. Các test dùng loopback, protobuf nhỏ và nguồn TikFinity giả
lập, cùng phiên TikTok thật được mô tả dưới đây. Đây không phải bằng chứng rằng
mọi phiên TikTok ngoài thực tế đều hoạt động.

## Lỗi tìm thấy và phần sửa

| Lỗi | Hành vi sau sửa |
|---|---|
| Snapshot Rust thiếu điểm tim, khác bản Node | Server giữ điểm theo người, gửi TOP và revision; reconnect không mất tim |
| TikFinity cộng cả các frame combo đang chạy | Pipeline chung chỉ nhận combo đã chốt, chống trùng trước khi học quà |
| Thêm người thủ công bị `keyword_only` chặn | Lệnh operator được vào sàn; member từ live vẫn theo chính sách tham gia |
| Người hết TTL vẫn được xem là đang ở sàn trước khi xử lý chat | Prune trước khi xét tham gia; điểm không phụ thuộc TTL |
| Hai control đổi nguồn có thể để sót tác vụ live/demo | Tuần tự hóa lệnh operator, sự kiện và reset |
| `demo_stop` làm trạng thái live thành idle | Chỉ đổi trạng thái nếu thực sự dừng demo |
| Master lỗi ghi đĩa vẫn đổi trong RAM; JSON sai có thể thay bằng default | Kiểm tra payload, ghi file tạm/rename trước khi áp dụng; lỗi giữ cấu hình cũ |
| Mất message do client chậm nhưng không đồng bộ lại | Đóng socket để client reconnect và lấy snapshot; giới hạn thời gian gửi |
| Nguồn TikFinity đứng im nhưng TCP vẫn mở | Ping/pong và timeout handshake, quay lại vòng reconnect |
| TikTok báo connected trước khi mở WebSocket | Chỉ báo sau handshake và các frame vào phòng, bao gồm reconnect |
| Gift JSON rất lớn gây tràn phép nhân trước sanitize | Nhân có giới hạn và loại chuỗi số không hữu hạn |
| `data.id` có thể là ID người xem nhưng được dùng chống trùng | Ưu tiên `eventId`/`msgId`; bare viewer ID không làm mất các lượt tim sau |

Thư viện quà được ghi bằng file tạm/rename. Khi shutdown, server dừng nguồn live/demo
và chờ lượt ghi đang chạy kết thúc trước khi ghi lần cuối.

## Kiểm chứng tự động

Lượt kiểm tra trên Windows: 89 test Rust và 30 test Node/WebSocket đều qua, không
bỏ qua test Rust qua WebSocket; build dev và Clippy thành công. Bản kiểm thử nằm ở
`server/target/debug/tiktok-server.exe`; chưa thay executable release đang chạy.

- `cargo test --locked`: nghiệp vụ, config thực tế và transport TikTok loopback.
- `cargo clippy --all-targets --locked`: kiểm tra tĩnh Rust.
- `TikTokBridge/test/points-wire.test.js`: chạy cùng hợp đồng trên Node và Rust;
  tim, combo đang chạy/chốt, bản tin trùng, TOP khi reconnect, manual VIP và reset.
- `TikTokBridge/test/rust-lifecycle-wire.test.js`: member/chat/follow/share/gift,
  keyword/giftAlwaysJoins, TTL/trần nhân vật, replay sau reconnect, hai control đổi
  nguồn, chuyển demo/live, Master lỗi ghi đĩa, input JSON sai, quyền overlay và rate
  limit. Burst gồm 1.000 lượt tim khác nhau và 500 bản tin trùng phải cho 1.000 điểm.
  Test thêm nguồn không trả pong qua đủ chu kỳ timeout thực tế.
- `TikTokBridge/test/display-wire.test.js`: cấu hình hiển thị, quyền ghi, reset,
  reconnect, restart và lỗi lưu file trên cả hai backend.

## Kiểm tra TikTok thật

Ngày 2026-09-20, tìm tài khoản đang live bằng API trạng thái phòng của TikTok rồi
chạy `TikTokBridge/scripts/check-rust-live.js` trên server/config/cổng riêng.
Tài khoản: [@pinkydollreal](https://www.tiktok.com/@pinkydollreal/live), room
`7687412807647251221`. Không gửi chat, tim, follow, share hay quà để tạo sự kiện.

Phiên 08:09:50–08:12:54 (UTC+7), quan sát 180 giây sau khi connected:

| Sự kiện nhận qua WebSocket game | Số bản tin |
|---|---:|
| member | 468 |
| chat | 155 |
| like | 127 |
| gift | 24 |
| follow | 28 |
| share | 88 |

Tổng 890 bản tin, tương ứng 1.311 tim và 721 kim cương quà. Có các combo 5, 31,
51 quà và Corgi 299 kim cương. Cả 157 cập nhật TOP khớp bộ tính điểm Node độc lập;
7 snapshot qua các kết nối overlay mới khớp đúng revision. Không có ID trùng lọt
qua, không thiếu userId/eventId, không phát sinh lỗi decode trong log. Đây là
đối chiếu theo bản tin nhận được, không phải đối soát ví quà của chủ phòng.

Phiên tiếp theo 08:18:09–08:19:15 nhận thêm đủ 6 nhóm sự kiện. Sau lệnh ngắt live,
không còn sự kiện mới; lệnh kết nối lại mở socket mới, nhận tiếp sự kiện thật và
TOP bắt đầu phiên mới. Tài khoản `@olachat.net` lúc 08:16:36 được TikTok báo không
live: server chuyển error/reconnecting, không báo connected hoặc phát sự kiện giả.

Sau sửa cuối về shutdown/ghi cấu hình, chạy lại lúc 08:20:47–08:21:53: thêm 247
bản tin đủ 6 nhóm; 64 đối chiếu điểm, 5 snapshot và kết nối lại live đều qua.
Binary của lượt này có cùng SHA-256 với bản debug cuối được build/test.

Báo cáo và log đầy đủ nằm tại `UnityProject/Logs/real-live-Vbg0J6`,
`real-live-cX46ST`, `real-live-TlecK1`, `real-live-vyqEQ1` (không commit dữ liệu người xem).
Số liệu tổng hợp: [live-verification-2026-09-20.json](live-verification-2026-09-20.json).
Đã thử Ctrl+C riêng khi demo đang chạy và client còn kết nối: client bị đóng,
server in `Đã dừng server.` và process kết thúc.

## Giới hạn cần biết khi đánh giá livestream

- Gameplay hiện nhận 6 nhóm: member, chat, like, gift, follow, share. PK/battle,
  link mic, subscription và các loại Webcast khác chưa có hành động game riêng.
- TikTok trực tiếp có nhánh `LiveEnded` và dừng retry khi nhận nhánh này. TikFinity
  hiện theo dõi kết nối Desktop; chưa có mapping riêng cho thông báo kết thúc
  phòng live trong payload Desktop. Desktop còn mở không chứng minh phòng còn live.
- Combo chỉ tính khi nhận frame chốt. Không thể khôi phục chắc chắn quà/tim mà
  provider không gửi trong thời gian mất mạng. Khử trùng có bộ nhớ giới hạn 2.000
  khóa, tối đa 10 phút với ID, 2,5 giây khi không có ID; không phải bảo đảm exactly-once.
- Điểm lưu trong RAM theo phiên, chưa có khôi phục điểm sau khi server crash hoặc
  khởi động lại. Trần nhân vật không giới hạn số người có điểm trong phiên.
- Đã kiểm chứng kết nối thật, đủ 6 nhóm sự kiện và kết nối lại bằng lệnh operator.
  Chủ phòng chưa tắt live trong thời gian quan sát; live-end, handshake bị chặn,
  nguồn im lặng và mất mạng được thử bằng protobuf/loopback, chưa phải sự cố thật
  từ phòng này. Phòng giới hạn tuổi và thay đổi giao thức phía TikTok chưa được
  kiểm chứng thực tế. Chưa có soak test nhiều giờ.

Không nên kết luận “bắt hết mọi case livestream” từ bộ test này. Các luồng game
được liệt kê đã có kiểm tra; các giới hạn trên cần dữ liệu/phiên live thực tế.
