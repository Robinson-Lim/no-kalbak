# no-kalbak

던전앤파이터 장비 툴팁을 인식해 실제 능력치를 비교하고, 칼레이도 박스 사용 판단을 돕는 Windows 프로그램입니다.
C# / .NET 10 / WPF로 작성했으며 Windows OCR과 PP-OCRv5 ONNX를 사용합니다.

## 시연 영상

[![no-kalbak 프로그램 시연 영상](https://img.youtube.com/vi/jsNqvN16rGI/hqdefault.jpg)](https://youtu.be/jsNqvN16rGI)

썸네일을 클릭하면 YouTube에서 시연 영상을 볼 수 있습니다. [▶ 시연 영상 보기](https://youtu.be/jsNqvN16rGI)

## 프로그램 파이프라인

Neople Open API로 캐릭터와 착용 장비 정보를 조회하고, 게임 화면의 툴팁에서 실제 능력치를 읽어 판정에 활용합니다.

```mermaid
flowchart TD
    A["캐릭터 조회·선택<br/>Neople Open API"] --> B["게임에서 장비에 마우스 올리기"]
    B --> C["커서 주변 화면 캡처"]
    C --> D["툴팁 영역 탐색·전처리"]
    D --> E["OCR 인식·결과 보정<br/>Windows OCR + PP-OCRv5 ONNX"]
    E --> F["필드 추출<br/>레어리티·부위·품질·주능력치"]
    F --> G["인게임 탭<br/>등급 판정·결과 표시"]
    F -->|착용 장비 등록 모드| H["현재 착용 장비와 매칭<br/>사용자 확인 후 저장"]
    A -.->|착용 장비 정보| H
    H --> I[("SQLite<br/>캐릭터별 실측값")]
    I --> J["현재 착용 장비의 실측값과<br/>100% 기준값 비교"]
    A -->|착용 장비 목록| J
    K["능력치 기준표<br/>stattable.json"] --> J
    K -.->|참고값| G
    J --> L["장비 탭<br/>비교 결과 표시"]
```

- **인식:** 빠른 1차 판독 후 정밀 인식으로 결과를 보정합니다. OCR은 PC에서 실행하며, 게임 화면을 외부 OCR 서버로 전송하지 않습니다.
- **인게임 판정:** `최상급` 여부를 우선 판단하고, 읽어 낸 능력치와 기준값을 함께 보여줍니다. 품질이 반드시 `100%`여야 최상급으로 판정되는 것은 아닙니다.
- **착용 장비 비교:** 등록 모드에서 실제 착용 아이템의 툴팁을 읽고 사용자가 저장합니다. 장비 탭에서는 현재 API 장비 정보와 일치하는 실측값만 비교하며, 저장값이 없거나 장비가 바뀌면 재인식을 요청합니다.

## 실행

Windows 10 2004(빌드 19041) 이상, x64 환경과 한국어 Windows OCR 언어 구성요소가 필요합니다.
배포 ZIP은 .NET 런타임을 포함합니다. 사용자가 .NET SDK를 설치할 필요는 없습니다.

1. 배포용 `no-kalbak-runtime.zip`을 **전체 압축 해제**합니다.
2. `config.json.sample`을 `config.json`으로 복사하고 본인의 Neople Open API 키를 입력합니다.
3. `DnfItemChecker.App.exe`를 실행합니다. `models/` 폴더도 실행 파일 옆에 있어야 합니다.

API 키는 `NEOPLE_API_KEY` 환경 변수로도 설정할 수 있습니다.
설정 파일, 캐릭터 DB, 로그는 실행 파일 옆에 저장되므로 쓰기 가능한 폴더에서 실행하세요.
자세한 안내는 [배포판 실행 안내](docs/RUNTIME.md)를 참고하세요.

### 사용 시 유의사항

실사용 테스트에서 **전체화면 모드에서도 장비 인식이 동작하는 것을 확인했습니다.**

> **창 모드에서는 반드시 게임 화면을 한 번 클릭해 게임 창을 활성화한 뒤 사용하세요.**
> 게임 창이 활성화되어 있어야 인식이 동작합니다. 프로그램이나 다른 창을 클릭한 뒤에는 게임 화면을 다시 클릭하고, 인식할 장비에 마우스를 올려주세요.

## 개발 및 빌드

Windows와 .NET 10 SDK가 필요합니다. 저장소 루트에서 실행합니다.

```powershell
dotnet test .\DnfItemChecker.slnx -c Release
.\build.ps1
$env:NEOPLE_API_KEY = "본인의 API 키"
.\run.ps1
```

스크립트 실행이 차단된 환경에서는 `powershell -ExecutionPolicy Bypass -File .\build.ps1`로 해당 실행에만 정책을 적용할 수 있습니다.

- 실행 파일: `artifacts/publish/DnfItemChecker.App.exe`
- 배포 ZIP: `artifacts/no-kalbak-runtime.zip`
- 무결성 확인: `artifacts/no-kalbak-runtime.zip.sha256`

빌드 스크립트는 실패 시 중단하며, 실행 파일·모델 4개·기준 능력치 표·빈 설정 예제·설명서만 ZIP에 넣습니다.
기존 실행 폴더에 개인 설정이나 DB가 생겨도 배포 ZIP에 포함하지 않습니다.

## 폴더 구성

| 경로 | 내용 |
|---|---|
| `src/DnfItemChecker.App/` | WPF 화면, 캐릭터 및 장비 등록 |
| `src/DnfItemChecker.Core/` | API, SQLite, 파싱, 능력치 비교 |
| `src/DnfItemChecker.Vision/` | 화면 캡처, 툴팁 탐지, OCR |
| `src/DnfItemChecker.Vision/models/` | 실행에 필요한 ONNX 모델과 한국어 사전, 총 약 18 MiB |
| `tests/` | Core, Vision, App 자동 테스트 |
| `tools/RecogProbe/` | 이미지 데이터셋 인식 및 라이브 캡처 검증 도구 |
| `stattable.json` | 배포에 사용하는 기준 능력치 표 |
| `docs/` | 검토 기록, 실행·업로드 안내, 과거 벤치마크 요약 |

## 향후 개선 과제 (Future Work)

현재 구현을 바탕으로 인식 정확도와 처리 성능을 높이고, 지원하는 장비의 범위를 확장할 예정입니다.

1. **인식 정확도 개선:** 다양한 화면 조건과 툴팁에서 발생하는 오인식을 줄이고, 장비 정보와 능력치를 더 정확하게 판독하도록 개선할 예정입니다.
2. **처리 성능 개선:** 화면 캡처부터 인식 결과 표시까지의 지연을 줄여, 연속으로 장비를 확인할 때의 응답성을 높일 예정입니다.
3. **미지원 장비 인식 확장:** **“이계의 기운이 서린” 장비는 현재 인식되지 않습니다.** 해당 장비의 툴팁도 처리할 수 있도록 인식 방식을 보완하고 지원할 예정입니다.

## 패치 노트

**1.0.1:** 반복 인식 중 취소/동시 호출로 종료되는 문제를 수정했습니다. [원인과 검증 결과](docs/OCR-CRASH-FIX.md)
