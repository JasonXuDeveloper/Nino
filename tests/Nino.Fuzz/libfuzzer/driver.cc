// A libFuzzer driver for SharpFuzz targets (Nino.Fuzz's "libfuzzer <target>" command).
//
// It speaks the protocol of SharpFuzz 2.x's Fuzzer.LibFuzzer.Run, the one the libfuzzer-dotnet project implements:
// the driver starts the .NET process with three environment variables,
//   __LIBFUZZER_SHM_ID           a System V shared memory id: 64 KB of edge counters, then the input;
//   __LIBFUZZER_CONTROL_PIPE_ID  the descriptor the target reads each input's length from (int32);
//   __LIBFUZZER_STATUS_PIPE_ID   the descriptor the target writes a status to (int32: 0 done, 2 exception);
// the target writes one status when it is ready and one after each input. The edge counters SharpFuzz's
// instrumentation increments are copied into libFuzzer's extra counters after each input, so libFuzzer is
// guided by the coverage of the managed code.
//
// Kept here (instead of downloading libfuzzer-dotnet's binary) so the fuzzer builds from source with clang and
// libFuzzer only: clang++ -O1 -fsanitize=fuzzer driver.cc -o nino-libfuzzer
//
// Usage: nino-libfuzzer --target_path=<program> [--target_arg=<arguments, space separated>] [libFuzzer flags] [corpus]
// (libFuzzer ignores flags that start with two dashes.)

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <signal.h>
#include <spawn.h>
#include <sys/ipc.h>
#include <sys/shm.h>
#include <sys/wait.h>
#include <unistd.h>

extern "C" char **environ;

static const size_t kMapSize = 1 << 16;
static const size_t kMaxInput = 1 << 20;

__attribute__((used, section("__libfuzzer_extra_counters"))) static uint8_t extra_counters[kMapSize];

static uint8_t *shared;
static int control_fd = -1; // we write input lengths
static int status_fd = -1;  // we read statuses
static pid_t child = -1;

static void die(const char *what) {
  perror(what);
  exit(1);
}

static bool read_all(int fd, void *buf, size_t n) {
  auto *p = static_cast<uint8_t *>(buf);
  while (n > 0) {
    ssize_t r = read(fd, p, n);
    if (r <= 0) return false;
    p += r;
    n -= static_cast<size_t>(r);
  }
  return true;
}

static void child_died() {
  int status = 0;
  if (child > 0 && waitpid(child, &status, 0) == child) {
    if (WIFSIGNALED(status))
      fprintf(stderr, "nino-libfuzzer: the target died of signal %d\n", WTERMSIG(status));
    else if (WIFEXITED(status))
      fprintf(stderr, "nino-libfuzzer: the target exited with code %d\n", WEXITSTATUS(status));
  } else {
    fprintf(stderr, "nino-libfuzzer: lost the target\n");
  }
  abort(); // libFuzzer saves the input as crash-<sha1>
}

static void kill_child() {
  if (child > 0) kill(child, SIGKILL);
}

extern "C" int LLVMFuzzerInitialize(int *argc, char ***argv) {
  std::string path, args;
  for (int i = 1; i < *argc; i++) {
    const char *a = (*argv)[i];
    if (strncmp(a, "--target_path=", 14) == 0) path = a + 14;
    else if (strncmp(a, "--target_arg=", 13) == 0) args = a + 13;
  }
  if (path.empty()) {
    fprintf(stderr, "usage: %s --target_path=<program> [--target_arg=<arguments>] [libFuzzer flags]\n", (*argv)[0]);
    exit(1);
  }

  int shm_id = shmget(IPC_PRIVATE, kMapSize + kMaxInput, IPC_CREAT | 0600);
  if (shm_id < 0) die("shmget");
  shared = static_cast<uint8_t *>(shmat(shm_id, nullptr, 0));
  if (shared == reinterpret_cast<uint8_t *>(-1)) die("shmat");
  shmctl(shm_id, IPC_RMID, nullptr); // freed once both processes detach

  int control[2], status[2];
  if (pipe(control) != 0 || pipe(status) != 0) die("pipe");
  control_fd = control[1];
  status_fd = status[0];

  std::vector<std::string> env_strings = {
      "__LIBFUZZER_SHM_ID=" + std::to_string(shm_id),
      "__LIBFUZZER_CONTROL_PIPE_ID=" + std::to_string(control[0]),
      "__LIBFUZZER_STATUS_PIPE_ID=" + std::to_string(status[1]),
  };
  std::vector<char *> env;
  for (auto &s : env_strings) env.push_back(&s[0]);
  for (char **e = environ; *e; e++) env.push_back(*e);
  env.push_back(nullptr);

  std::vector<std::string> words = {path};
  size_t at = 0;
  while (at < args.size()) {
    size_t space = args.find(' ', at);
    if (space == std::string::npos) space = args.size();
    if (space > at) words.push_back(args.substr(at, space - at));
    at = space + 1;
  }
  std::vector<char *> argv_child;
  for (auto &w : words) argv_child.push_back(&w[0]);
  argv_child.push_back(nullptr);

  posix_spawn_file_actions_t actions;
  posix_spawn_file_actions_init(&actions);
  posix_spawn_file_actions_addclose(&actions, control[1]);
  posix_spawn_file_actions_addclose(&actions, status[0]);
  if (posix_spawnp(&child, path.c_str(), &actions, nullptr, argv_child.data(), env.data()) != 0) die("posix_spawn");
  posix_spawn_file_actions_destroy(&actions);
  close(control[0]);
  close(status[1]);
  atexit(kill_child);
  signal(SIGPIPE, SIG_IGN);

  int32_t ready;
  if (!read_all(status_fd, &ready, sizeof ready)) child_died();
  memset(shared, 0, kMapSize);
  return 0;
}

extern "C" int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size) {
  if (size > kMaxInput) return 0;
  memcpy(shared + kMapSize, data, size);
  int32_t length = static_cast<int32_t>(size);
  if (write(control_fd, &length, sizeof length) != sizeof length) child_died();
  int32_t status;
  if (!read_all(status_fd, &status, sizeof status)) child_died();
  memcpy(extra_counters, shared, kMapSize);
  memset(shared, 0, kMapSize);
  if (status != 0) {
    fprintf(stderr, "nino-libfuzzer: the target reported a finding (its exception is above)\n");
    abort();
  }
  return 0;
}
