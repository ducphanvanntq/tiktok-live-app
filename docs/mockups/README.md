# Sàn nhảy & ánh sáng — mock UI

Mở `server/public/club-mock.html` trực tiếp bằng trình duyệt (không cần cài thêm thư viện).
Nếu đang chạy Rust server của dự án, truy cập `http://127.0.0.1:8085/club-mock.html`.

Bản xem thử có sàn LED với ba kiểu chuyển động, ba phối màu, đèn quét, laser, khách mô phỏng,
điều chỉnh độ sáng / tốc độ, hiệu ứng DROP, tạm dừng và toàn màn hình.

Đây là mock giao diện độc lập bằng Canvas 2D, chưa kết nối điều khiển đèn Unity hoặc TikTok.
BPM chỉ điều chỉnh tốc độ mô phỏng, không phát nhạc. Thiết lập giảm chuyển động của hệ điều hành
sẽ khiến bản xem thử khởi động ở trạng thái tạm dừng.

Ảnh xem nhanh: `club-mock-desktop.png` và `club-mock-mobile.png`.

## Bản Unity chạy thật

Chạy `run-led-preview.bat` ở thư mục gốc để mở bản game riêng với 20 NPC, không kết nối TikTok.
Sàn LED được thêm vào cảnh Unity mặc định, dưới chân nhân vật; 192 ô sáng được vẽ bằng một
quad và một shader. Nhịp dùng `ClubBeatClock`, cùng BPM với hệ thống đèn; chưa phân tích beat
từ âm thanh. Bảng điều khiển web ở trên vẫn là mock độc lập.

- **F6**: đổi sáu phối màu (thay đổi tạm trong game).
- **F7**: đổi làn sóng / ô cờ / lan tỏa.
- **F8**: tắt / bật ánh sáng sàn.

Build bằng Unity 6000.2.10f1 (thay đường dẫn Unity theo máy):

```powershell
& 'D:\Unity\Editors\6000.2.10f1\Editor\Unity.exe' -quit -batchmode -projectPath "$PWD\UnityProject" -buildWindows64Player "$PWD\UnityProject\Builds\LedPreview\TikTokBarGame.exe" -logFile "$PWD\UnityProject\Logs\led-floor-build.log"
```

Bản preview dùng các thư mục `LiveAssets`, `DJ_VIDEO`, `DJ_MUSIC` đặt cạnh executable
(máy phát triển hiện tại dùng junction tới thư mục media gốc).

Kiểm tra GPU và chụp ảnh trong player:

```powershell
& '.\UnityProject\Builds\LedPreview\TikTokBarGame.exe' -welcomePreviewPath "$PWD\docs\mockups" -ledFloorPreview -logFile "$PWD\UnityProject\Logs\led-floor-check.log"
```

Kiểm tra so sánh pixel shader ở các nhịp, phối màu và mức sáng khác nhau; xác nhận nhịp chạy
theo thời gian, vị trí sàn và 20 NPC. Kết quả ghi vào `unity-led-verification.txt`, ảnh chụp
game vào `unity-led-1.png` đến `unity-led-3.png` và `unity-led-off.png`.

## Điều khiển sân khấu thật trên web

Chạy **`run-stage-preview.bat`** ở thư mục gốc. Launcher mở bản Unity mới và Rust server,
sau đó mở `http://127.0.0.1:8085/control.html#lighting`. Khác với `run-led-preview.bat`
offline, bản này có kết nối để nhận thay đổi từ web; chưa kết nối TikTok cho tới khi bạn
chủ động nhập tài khoản và bấm kết nối.

Tab **Sân khấu & đèn** có:

- Bật/tắt riêng mặt sàn LED, nền tường/trần và hệ thống đèn sân khấu.
- Sáu phối màu: neon, hoàng hôn, xanh băng, cầu vồng động, hồng kẹo, xanh ngọc.
- Độ sáng riêng từng phần từ 0–200%; chùm đèn rộng từ 0,5–4×.
- Ba hiệu ứng sàn; chọn giữ nguyên màu ảnh nền nếu muốn.
- Nút phối cảnh nhanh đổi đồng bộ ba nhóm màu, giữ nguyên bật/tắt.

Thả thanh trượt để lưu. Server lưu vào `server/config/display.json`, gửi đến mọi client
và đồng bộ lại sau reconnect / restart. Chế độ chroma F2 vẫn ưu tiên ẩn môi trường;
thoát chroma sẽ phục hồi theo các công tắc web. Đèn quà tặng thuộc mục hiệu ứng quà riêng.
Các điều khiển này dành cho Rust server mới; Node bridge cũ không có tính năng ánh sáng.

Build server dùng cho launcher bằng `cargo build --manifest-path server/Cargo.toml`.
Build Unity bằng lệnh ở trên. Bản phát hành `Build/TikTokBarGame.exe` cũ không tự thay đổi;
hãy dùng launcher preview hoặc build lại gói phát hành.

Kiểm tra xuyên suốt web → Rust → Unity (cần cổng 8085 trống, Windows và Edge):

```powershell
node TikTokBridge/scripts/check-stage-lighting.js
```

Script dùng bản sao cấu hình trong thư mục tạm, kiểm tra renderer thật, tăng độ sáng,
độ rộng chùm đèn, sáu phối màu, các công tắc và lưu qua restart. Xem kết quả và ảnh
trong `docs/mockups/stage-lighting/`. Không suy ra FPS từ kết quả này.

## Chạy build / release thủ công trên GitHub Actions

Sau khi commit có workflow được push lên nhánh mặc định:

1. Vào **Actions → Build Windows** hoặc **Build macOS → Run workflow** để build ZIP
   và tải artifact, chưa tạo GitHub Release. Chọn nhánh/tag và nhập version nếu cần.
2. Vào **Actions → Release → Run workflow** để build cả hai nền tảng và tạo release.
   Nhập version mới dạng `v1.0.6` hoặc `v1.0.6-rc.1`; chọn `prerelease` nếu là bản thử.
3. Tag chưa có sẽ được tạo tại commit được chọn để build. Nếu tag đã có, phải chọn đúng
   commit của tag đó. Workflow từ chối release trùng và tag trỏ sang commit khác.

Các build dùng chung secrets Unity hiện có. Push tag `v*` vẫn chạy release tự động.
Thêm nút manual không tự chạy workflow hoặc publish; commit local cần được push trước.
