# 배포 검증 · 2026-09-05

대상은 1.0.1 소스와 Windows x64 배포 ZIP입니다. 이번 변경 범위는 README·문서·그래프 표기이며 애플리케이션 소스는 변경하지 않았습니다.

## 자동 테스트

`dotnet test .\DnfItemChecker.slnx -c Release --no-restore` 첫 실행 결과:

| 프로젝트 | 통과 | 실패 |
|---|---:|---:|
| Core | 91 | 0 |
| App | 6 | 0 |
| Vision | 40 | 1 |

Vision의 `LocatorFindsRelativeTooltipAtValidationResolutions(1280, 720)`에서 테스트 입력 이미지를 BMP로 저장하던 중 `ArgumentNullException (encoder)`가 발생했습니다. 위치는 `TooltipGeometryTests.Encode`의 `Bitmap.Save`입니다.

동일 바이너리로 Vision 프로젝트만 재실행했을 때는 **41/41 통과**했습니다. 첫 실패의 원인은 확정되지 않았으며 재실행 성공만으로 해결된 것으로 간주하지 않습니다. 실제 게임에서 보고된 OCR 중복 호출 오류와 동일한 원인인지도 확인되지 않았습니다.

## 빌드·패키지

- `build.ps1`의 Release / win-x64 / self-contained 게시와 ZIP 생성 성공.
- 빈 API 키 설정 예제, 실행 파일, ONNX 모델 3개, 한국어 사전, 기준표, 실행 안내로 배포 구성.
- 그래프 JSON과 1.0.1 측정 결과의 핵심 수치 일치, 문서 상대 링크 확인.
- 과거 143장 인식 결과는 2026-09-04 측정값이며, 이번 문서 변경 검증에서 다시 측정한 값이 아닙니다.

## 알려진 문제

빌드에서 `SQLitePCLRaw.lib.e_sqlite3` 2.1.11의 `NU1903` 경고가 발생합니다. 관련 권고는 [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)입니다. DB 의존성 갱신과 회귀 검증이 필요합니다.

장시간 실제 게임 사용, 다양한 DPI·해상도에서의 캡처부터 화면 표시까지의 응답 시간은 추가 검증 대상입니다.
