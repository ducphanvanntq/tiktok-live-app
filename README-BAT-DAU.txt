WANGNGUEN-BRIGDE LIVE - FULL SOURCE, GIAY PHEP MIT

Noi dung goi:
- UnityProject: toan bo source Unity, scene, script va asset cua game.
- server: Rust bridge, TikTok/TikFinity, Control va Master Rules.
- TikTokBridge: ban Node.js cu, giu lam duong lui khi phat trien.
- DJ_MUSIC: tha file MP3, WAV hoac OGG cua ban vao day.
- DJ_VIDEO: tha video MP4 hoac anh PNG/JPG cua ban vao day.
- LiveAssets: cac anh huong dan co the dat trong TikTok LIVE Studio.

Yeu cau:
- Goi phat hanh: khong can cai them gi. Server la 1 file exe don le.
- TikFinity neu su dung LIVE_PROVIDER=tikfinity.
- Rust 1.85 tro len va Unity 6000.2.10f1 chi can khi tu build source.

Chay nhanh tren Windows:
1. Tai file WangnguenBrigde-Live-Windows-v*.zip moi nhat trong muc Releases.
2. Giai nen toan bo ZIP ra thu muc moi. Khong chay truc tiep ben trong ZIP.
3. Nhan dup run.bat.
4. Neu cong 8085 dang bi chiem, dong dung chuong trinh duoc bao roi chay lai.
   Launcher khong tu tat chuong trinh khac de tranh mat du lieu.

Dau hieu thanh cong:
- Cua so TikTok Server hien http://127.0.0.1:8085.
- Control Panel mo tren trinh duyet va logo hien binh thuong.
- Game mo va ket noi server thanh cong.
- Goi Windows da kem Build\DJ_MUSIC\nhacnen.MP3; co the thay bang nhac cua ban.

Luu y: nut "Source code (zip)" cua GitHub KHONG co file game Build.
Nguoi chi muon chay game phai tai dung file Windows trong muc Releases.

Chay server thu cong:
1. Mo PowerShell tai thu muc server.
2. Chep .env.example thanh .env
3. Chay: cargo run
4. Mo: http://127.0.0.1:8085/control.html

Mo source Unity:
1. Mo Unity Hub.
2. Add project from disk va chon thu muc UnityProject.
3. Mo scene trong Assets/Scenes.
4. Dung dung Unity 6000.2.10f1 va chay build.bat.

Build toan bo va dong goi:
- Chay build.bat: build server Rust, build game Unity, roi tao file ZIP
  phat hanh trong thu muc dist.
- Chi dong goi lai tu ban build co san: bash scripts/package-windows.sh

Ban source nay khong co khoa may, khong khoa TikTok, khong can kich hoat.
Nguoi dung co the tu them he thong license, doi ten, sua code va build san pham.

Luu y:
- Chi su dung va chia se nhac khi ban co quyen hop le.
- Tai nguyen ben thu ba van phai tuan theo dieu khoan cua nha cung cap.
- Xem file LICENSE de biet chi tiet giay phep MIT.

Lien he:
- Ma nguon: https://github.com/ducphanvanntq/tiktok-live-app
- Email   : wangnguenlc79@gmail.com
