# TOP điểm

Bảng dùng asset trong `UnityProject/Assets/Resources/TopPoints/`, hiển thị tối đa
3 người có điểm dương ở góc dưới phải. Bề rộng khoảng 31% màn hình dọc. Tên và
điểm là chữ động, avatar lấy qua `AvatarService` và có ảnh dự phòng. Không có
người ghi điểm thì bảng ẩn hoàn toàn; nếu chỉ có 1–2 người thì các hàng còn lại
để trống. `F3` bật/tắt bảng. Header cũ “ÔNG CHÚ MMO” không còn hiển thị trên sàn.

## Điểm và phiên

- 1 tim = 1 điểm; 1 kim cương quà = 100 điểm. Ví dụ 300 tim + 5 kim cương = 800.
- `diamondCount` đã là tổng giá trị combo sau chuẩn hóa; không nhân thêm
  `repeatCount`. TikTok và TikFinity chỉ tính combo đã kết thúc, sau lọc sự kiện trùng.
- Bằng điểm: người đạt mức đó trước đứng trên. NPC và người có 0 điểm bị loại.
- Người chưa vào sàn vẫn có thể ghi điểm; ghi điểm không bỏ qua luật vào sàn.
  Huy hiệu trên sàn giữ đúng hạng, kể cả khi hạng cao hơn thuộc người ngoài sàn.
- Bridge giữ điểm trong RAM của phiên, độc lập thời hạn hiện diện trên sàn.
  Unity kết nối lại nhận tổng tuyệt đối qua snapshot, không cộng lại sự kiện.
  Reset game, đổi phiên hoặc khởi động lại Bridge xóa điểm. Chưa có lưu điểm ra đĩa.
- Các thống kê kim cương và quy tắc hiệu ứng quà vẫn dùng giá trị kim cương gốc.
- Các nút thêm VIP/tặng quà thủ công trong Unity gửi qua Bridge khi đang kết nối,
  nên điểm quà thử được đồng bộ và giữ lại khi overlay kết nối lại.

## Hiển thị và animation

Hàng đổi hạng mờ đi trước khi xuất hiện ở vị trí mới; số điểm chạy đến tổng mới.
Người mới vào TOP có hiệu ứng hiện dần, người rời TOP mờ đi. Các cập nhật dồn dập
tiếp tục từ trạng thái hiện tại và kết thúc ở thứ hạng mới nhất.

Bảng ẩn ngay khi vùng bao của nhân vật, cánh hoặc nhãn chạm vùng bảng, sau khi
camera cập nhật. Bảng hiện lại sau 0,5 giây không bị che và fade trong 0,2 giây.
Animation hàng tạm dừng trong lúc bảng bị ẩn. Bảng cũng nhường chỗ cho welcome,
banner quà và bảng điều khiển. Kiểm tra vùng bao có chủ ý thận trọng: bảng có thể
ẩn dù chỉ phần trong suốt của sprite chạm vùng bảng. Khi góc này đông người,
bảng có thể bị ẩn lâu. Feed được giới hạn bề rộng để không lấn sang bảng.

## Kiểm tra

Chạy `npm test` trong `TikTokBridge/`. Các test điểm kiểm tra công thức, TOP 3,
thứ tự khi bằng điểm, đổi tên, giới hạn số an toàn và reset. Test wire mở Bridge
trên cổng riêng cùng nguồn TikFinity giả cục bộ; xác nhận lọc combo đang chạy,
lọc sự kiện trùng, tổng điểm qua reconnect và reset. Không cần kết nối LIVE.

Build Unity bằng editor `6000.2.10f1`, rồi chạy EXE với các tham số:

```text
-screen-fullscreen 0 -screen-width 1080 -screen-height 1920
-welcomePreviewPath <absolute-output-directory> -topPointsPreview
-logFile <absolute-log-file>
```

Các dòng trên thuộc cùng một lệnh. Chế độ này không tạo kết nối WebSocket tới
Bridge đang chạy. Harness kiểm tra asset, bảng trống/một người/ba người, thứ
hạng trên sàn, đổi hạng và cập nhật dồn dập, reconnect/reset, tên dài/điểm lớn,
viewport dọc và ảnh xuất 2x, nhân vật che bảng và tám góc camera với nhảy/đi vòng/phóng to.
Ảnh `change-00.png` đến `change-08.png` ghi lại chuyển hạng để kiểm tra hình ảnh.
`verification.txt` và log `TOP_POINTS_PREVIEW_OK` xác nhận kiểm tra thành công;
lỗi kết thúc với mã 2. Ảnh/log/build là đầu ra cục bộ, không đưa vào asset commit.
