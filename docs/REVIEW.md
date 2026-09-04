# GitHub 업로드 정리 검토 — 2026-09-04

## 기준과 범위

`OCR 대체 방안 비교` 대화와 실제 로컬 파일을 대조했습니다.
`fourth/` 최신 소스의 인식 동작을 기준으로 소스 저장소와 배포 파일을 분리했습니다.
과거 작업 폴더와 외부 데이터셋은 보존했습니다.

## 확인한 문제와 수정

| 발견 사항 | 처리 |
|---|---|
| `no-kalbak/`이 비어 있음 | 최신 소스·테스트·모델·기준표·RecogProbe 복원 |
| `github-upload/`에 소스와 약 186 MiB EXE 혼재 | Git에는 소스, 실행 파일은 Release ZIP으로 분리 |
| 기준 능력치 표가 재빌드 산출물에 자동 포함되지 않음 | 프로젝트의 build/publish 콘텐츠로 지정 |
| 모델이 single-file 내부에 들어가는지 명확하지 않음 | 외부 `models/` 배치를 프로젝트 설정으로 명시 |
| 빌드 실패 후에도 성공 메시지 출력 가능 | native exit code 검사 후 즉시 중단 |
| 실행 폴더를 압축하면 API 키·DB·로그 포함 가능 | 필요한 8개 파일만 새 staging 폴더에 복사해 압축 |
| `.gitignore`가 `app/` 일부 경로의 설정만 제외 | 모든 깊이의 설정·DB·로그·캡처·빌드 산출물 제외 |
| 벤치마크 도구와 기준 결과 누락 | RecogProbe와 개인 절대 경로를 제거한 기준 결과 보관 |
| Git 저장소·원격 연결 미완성 | 독립 `main` 저장소와 기존 GitHub origin 준비 |

이동 전 소스·테스트·모델 등 97개 파일의 SHA-256을 비교했고 `fourth/`와 `github-upload/`가 동일했습니다.
프로그램의 C# 인식 로직은 변경하지 않았습니다. 앱 프로젝트 파일에 배포 콘텐츠 설정을 추가했고,
Git 검사에서 지적한 기존 파일 3개의 불필요한 공백만 정리했습니다.

## 검증

- 전체 기존 테스트: **134/134 통과**, 실패·건너뜀 없음 (Core 91, Vision 37, App 6).
- RecogProbe Release 빌드 성공.
- Windows x64 self-contained single-file publish 성공.
- ZIP 구성: EXE, ONNX 3개, 사전 1개, 기준표, 빈 설정 예제, README — 총 8개 파일.
- 기존 전체 143장 재측정: **핵심 필드 143/143, 능력치 값 176/176**, 실패 0건.
- 재측정 지연: 평균 **502ms**, P50 490ms, P95 611ms, P99 749ms. 1회 측정이며 속도 개선 작업은 하지 않았습니다.
- ZIP을 별도 폴더에 풀어 파일별 SHA-256이 빌드 결과와 일치함을 확인했습니다.
- 압축 해제한 배포판 실행: WPF 창 표시, ONNX 초기화 OK, SQLite 생성, 정상 종료(exit 0) 확인.
  테스트용 더미 키를 사용했으며 Neople API 조회와 실제 게임 조작은 하지 않았습니다.
- 빌드 실패(exit 37)를 모의 실행했을 때 즉시 중단하고 기존 배포 ZIP을 바꾸지 않는 것을 확인했습니다.
- Git 업로드 후보에서 기존 설정의 API 키 일치 0건, 100 MiB 초과 파일 0건.
- 개인 설정·DB·빌드 ZIP·디버깅 캡처가 Git에서 제외됨을 확인했습니다.

재측정 요약: [profile_upload_review.json](benchmarks/profile_upload_review.json).

## 남은 문제

`SQLitePCLRaw.lib.e_sqlite3` 2.1.11의 NU1903 경고가 기존 소스와 동일하게 재현됩니다.
[공식 보안 권고](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)는 해당 패키지 계열에 patched version을 제시하지 않으며 SQLite 3.50.2 이상을 권고합니다.
경고를 숨기지 않았고 이번 기준 버전 정리에서 DB 의존성 교체를 섞지 않았습니다. 별도의 패키지 전환 및 SQLite 회귀 검증이 필요합니다.

기존 143장 벤치마크 결과는 모든 게임 환경의 정확도를 보장하지 않습니다.
실제 게임에서의 커서 정지부터 UI 표시까지의 지연, 다양한 DPI·해상도 검증 및 P95 300ms 최적화는 별도 작업입니다.

GitHub 원격 업로드에는 이 PC의 Git Credential Manager 로그인이 필요합니다.
소스 저장소 업로드와 Release 등록 방법은 [업로드 안내](GITHUB.md)를 참고하세요.
