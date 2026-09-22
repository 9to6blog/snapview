# 포맷 후 개발 환경 복구

이 문서는 저장소의 `SnapView.csproj`, `installer/SnapViewSetup.csproj`,
`tests/SelfTest/SelfTest.csproj`, `build.bat`를 기준으로 새 Windows PC에서
소스와 빌드 환경을 복구하는 순서를 정리한다. 경로는 원하는 작업 폴더로 바꾼다.

## 필요한 도구

- Windows x64 데스크톱 환경과 Git.
- **Windows x64용 .NET 8 SDK**. 2026-09-22에 확인한 기존 개발 환경은
  SDK `8.0.424`, Windows Desktop Runtime `8.0.30`이었다. 이는 확인한 기준 환경이며,
  저장소에는 SDK를 고정하는 `global.json`이 없다.
- 프로젝트는 `net8.0-windows`를 사용하며 WPF/WinForms와 Windows 네이티브 API를
  호출한다. 본체의 `PlatformTarget`과 배포 RID는 x64이다.
- 현재 프로젝트 파일에는 외부 `PackageReference`가 없다. 첫 restore/publish에서
  .NET targeting/runtime pack을 받아야 할 수 있으므로 필요한 팩이 준비되지 않은
  새 PC에서는 인터넷 연결이 필요하다.

기본 배포는 `--self-contained false`이므로 **본체와 설치 프로그램 모두
.NET 8 Desktop Runtime x64가 필요하다**. SDK가 없는 실행용 PC에도 이 런타임을
먼저 준비한다. 일반 .NET Runtime만 설치한 상태와 구분한다.

## 소스 복구와 빌드

새 작업 폴더의 PowerShell에서 다음과 같이 clone한다.

```powershell
git clone https://github.com/9to6blog/snapview.git SnapView
Set-Location .\SnapView
```

이미 `.git`을 포함해 보존한 작업 폴더를 복구했다면 clone을 생략하고 그 폴더로
이동한다. 필요한 커밋은 인계 기록과 대조한다. 아래 명령은 프로젝트 루트에서 실행한다.

```powershell
git status --short
git log -1 --oneline
dotnet --list-sdks
dotnet --list-runtimes
dotnet restore .\SnapView.csproj
dotnet build .\SnapView.csproj -c Debug --nologo
```

필수 입력은 본체 소스, XAML, `app.manifest`, `assets/app.ico`, `Core`, `Native`,
`Capture`, `Editor`, `Prefs`, `Viewer`, `tests`, `tools`, `installer`와 각 프로젝트
파일이다. 모두 저장소에서 관리한다. `bin`, `obj`, `dist`, `setup`은 `.gitignore`가
제외하는 빌드 산출물이며 새 빌드에서 생성한다.

## 자체 검사

```powershell
dotnet run --project .\tests\SelfTest -c Release --nologo
```

`build.bat selftest`도 같은 명령을 실행한다. 콘솔의 실패 항목과 프로세스 종료
코드를 확인한다. 이 검사는 Windows 데스크톱에서 캡처, WGC, 미디어 인코딩,
오디오, 클립보드와 파일 연결을 실제로 사용한다. 투명한 검사 창을 만들고,
클립보드 내용과 임시 파일/휴지통을 변경하며, 조건에 따라 현재 사용자의 파일
연결 레지스트리를 등록했다 해제한다. 로그인이 가능한 새 개발 환경에서 실행하고,
서버나 데스크톱 세션이 없는 환경의 결과를 같은 기준으로 해석하지 않는다.

코드 검사가 통과한 뒤 실제 앱에서 캡처, 편집 저장, 뷰어, 녹화와 탐색기 우클릭
연결을 확인한다. 이 문서를 작성할 때 새 Windows 설치나 clean-clone 빌드를
실행한 것은 아니며, 위 명령은 현재 소스와 스크립트를 대조해 정리했다.

## 배포 파일과 설치 프로그램

```powershell
.\build.bat release
# 결과: dist\SnapView.exe

.\build.bat installer
# 결과: setup\SnapView-Setup.exe
```

`installer` 명령은 본체를 먼저 publish한 뒤 `dist/SnapView.exe`를 설치 프로그램의
리소스로 포함한다. 따라서 빈 clone에서 설치 프로젝트만 단독 빌드하지 말고 이
명령을 사용한다. 설치 파일 생성은 설치 실행과 별개다.

스크립트는 본체와 설치 프로그램을 `win-x64`, Release, single-file,
`--self-contained false`로 publish한다. 실행 중인 이전 `dist/SnapView.exe`는
`SnapView.old.exe`로 이름을 바꿀 수 있으므로 새 산출물을 확인한다.

아이콘을 다시 만들 필요가 있을 때만 아래 명령을 사용한다. 아이콘 생성기도
저장소에 포함되어 있다.

```powershell
.\build.bat icon
```

## 사용자 설정과 작업 파일

Git 소스와 사용자 데이터는 별도로 보존한다. 앱을 종료한 뒤 필요한 파일을
백업하고, 복구할 때는 새 계정의 해당 경로에 넣는다.

| 위치 | 내용 |
| --- | --- |
| `%APPDATA%\SnapView\settings.json` | 설정, 단축키 등 |
| `%APPDATA%\SnapView\log.txt` | 사용 기록; 열어 본 파일 경로가 포함될 수 있음 |
| `%LOCALAPPDATA%\SnapView\recover.snapview` | 남아 있을 경우 편집 작업 자동 복구 파일 |
| 사용자가 선택한 저장 폴더 | 원본 이미지/동영상, 저장한 결과물, `.snapview` 프로젝트 |

설정에 저장한 경로가 새 PC에서도 존재하는지 확인한다. 파일 연결, 시작 프로그램,
바로가기는 소스나 설정 파일만 복사해 등록되지 않는다. 설치 프로그램 또는 앱의
설정 기능으로 필요한 항목을 다시 등록한다. 사용자 데이터와 개인 경로를 Git에
추가하지 않는다.
