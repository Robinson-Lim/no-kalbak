# no-kalbak

던전앤파이터 장비 툴팁을 인식해 실제 능력치를 비교하고, 칼레이도 박스 사용 판단을 돕는 Windows 프로그램입니다.
C# / .NET 10 / WPF로 작성했으며 Windows OCR과 PP-OCRv5 ONNX를 사용합니다.

## 실행

Windows 10 2004(빌드 19041) 이상, x64 환경과 한국어 Windows OCR 언어 구성요소가 필요합니다.
배포 ZIP은 .NET 런타임을 포함합니다. 사용자가 .NET SDK를 설치할 필요는 없습니다.

1. 배포용 `no-kalbak-runtime.zip`을 **전체 압축 해제**합니다.
2. `config.json.sample`을 `config.json`으로 복사하고 본인의 Neople Open API 키를 입력합니다.
3. `DnfItemChecker.App.exe`를 실행합니다. `models/` 폴더도 실행 파일 옆에 있어야 합니다.

API 키는 `NEOPLE_API_KEY` 환경 변수로도 설정할 수 있습니다.
설정 파일, 캐릭터 DB, 로그는 실행 파일 옆에 저장되므로 쓰기 가능한 폴더에서 실행하세요.
자세한 안내는 [배포판 실행 안내](docs/RUNTIME.md)를 참고하세요.

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

## 현재 상태

**1.0.1:** 반복 인식 중 취소/동시 호출로 종료되는 문제를 수정했습니다. [원인과 검증 결과](docs/OCR-CRASH-FIX.md)

`fourth/`의 최신 작업 소스에서 분리한 기준 버전입니다.
과거 저장된 벤치마크는 핵심 필드 143/143, 능력치 값 176/176, balanced 평균 약 485ms / P95 약 567ms입니다.
이 수치는 해당 데이터셋 결과이며 모든 게임 화면의 정확도를 뜻하지 않습니다. 실시간 UI 전체 지연과도 다릅니다.
P95 300ms 목표는 아직 달성하지 못했습니다.

정리 과정의 실제 검증 결과와 남은 문제는 [검토 기록](docs/REVIEW.md), 재측정 방법은 [벤치마크 안내](docs/BENCHMARKS.md)에 정리합니다.
