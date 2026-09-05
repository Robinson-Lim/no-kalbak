# 인식 벤치마크

[RecogProbe](../tools/RecogProbe/Program.cs)는 라벨이 있는 게임 캡처에서 필드 일치율과 인식 호출 지연을 측정합니다. 게임 캡처와 정답 데이터는 저장소에 포함되어 있지 않으며, 실행에는 별도의 이미지·JSONL 라벨이 필요합니다.

## 1.0.1 회귀 평가

| 지표 | 결과 | 단위 |
|---|---:|---|
| 핵심 필드 일치 | 143/143 | 이미지 |
| 능력치 값 일치 | 176/176 | 라벨의 개별 능력치 |
| 평균 인식 지연 | 458.54ms | 호출 |
| P50 / P95 / P99 | 446.61 / 547.99 / 690.94ms | 호출 |

[측정 결과 JSON](benchmarks/v1.0.1-regression.json) · [동시성 수정 검증](OCR-CRASH-FIX.md)

- **핵심 필드:** 레어리티, 부위, 품질 퍼센트, 능력치 이름 집합과 값이 모두 일치해야 한 장을 통과 처리합니다. 등급 문자열은 별도 필드로 평가하며 **아이템 이름은 제외**합니다.
- **능력치 값:** 한 이미지에 여러 능력치가 있으면 각각 집계합니다. 따라서 이미지 143장과 값 176개의 분모가 다릅니다.
- **지연:** 첫 이미지로 워밍업한 뒤 `RecognizeAsync` 호출을 측정합니다. 파일 읽기는 `capture`에 별도 기록하며, 표의 `latencyMs`에는 포함하지 않습니다. 화면 캡처·커서 대기·WPF 표시·초기 모델 로딩도 제외합니다.
- **해석:** 기존 데이터셋에 대한 1회 회귀 실행입니다. 새로운 게임 환경에 대한 일반화 정확도나 장시간 실사용 안정성을 입증하는 결과는 아닙니다. CPU·OS·OCR 언어팩 버전이 보고서에 기록되지 않아 다른 PC의 지연과 직접 비교할 수 없습니다.

## 데이터셋 평가 실행

Windows와 .NET 10 SDK, 한국어 Windows OCR 구성요소가 필요합니다. 저장소 루트에서 실행하며 `$labels`는 준비한 JSONL 경로로 지정합니다.

```powershell
$labels = "C:\datasets\dnf\labels.jsonl"
dotnet build .\tools\RecogProbe\RecogProbe.csproj -c Release
New-Item -ItemType Directory -Path .\artifacts -Force
dotnet run --project .\tools\RecogProbe\RecogProbe.csproj -c Release --no-build -- --labels $labels --report .\artifacts\benchmark.json --failure-report .\artifacts\failures.json
```

JSONL은 한 줄에 이미지 하나를 기술합니다. `image`는 JSONL 파일 기준 상대 경로이며, 필수 필드는 `rarity`와 `slot_kor`입니다. 정밀한 평가에는 `grade`, `quality_pct`, `main_stat`, `stats` 정답도 포함해야 합니다. 다음은 형식 예시입니다.

```json
{"image":"images/sample.png","rarity":"에픽","slot_kor":"팔찌","grade":"최상급","quality_pct":100,"main_stat":"힘","stats":{"힘":153}}
```

기본값은 hybrid / Balanced 모드입니다. `--include-item-name`을 추가하면 `item_name` 라벨도 평가합니다. `--immediate`는 빠른 1차 응답을 위한 별도 모드로, 일부 필드가 비어 있을 수 있습니다.

## 라이브 캡처 검증

실시간 실행 중 기록한 캡처와 검증 메타데이터를 준비한 뒤 실행합니다.

```powershell
dotnet run --project .\tools\RecogProbe\RecogProbe.csproj -c Release -- --validate-live C:\datasets\dnf\live-captures --profiles .\tools\RecogProbe\live-acceptance.json --min-trials 30
```

검증 조건은 [프로필](../tools/RecogProbe/live-acceptance.json)과 [검증기](../src/DnfItemChecker.Vision/LiveCaptureValidator.cs)를 참고하세요. 표본이 부족해 `INCONCLUSIVE`가 나오면 통과로 간주하지 않습니다.

## 과거 성능 변화

[그래프 원자료](benchmarks/performance-history.json) · [과거 평가 요약](benchmarks/HISTORY.md)

Python 153장 평가는 같은 평가셋을 반복 사용한 파이프라인 개선 기록입니다. C# 20장 지연 평가, 1.0.1의 143장 회귀 평가와는 데이터·필드·측정 경로가 다릅니다. 과거 244ms 고속 경로는 라이브 정확도 문제가 뒤따라, 정확도 보완 후 315ms 경로로 이어졌습니다.

이전 C# 기준 결과는 [전체 143장](benchmarks/profile_all_final3.json)에 보관되어 있습니다.
