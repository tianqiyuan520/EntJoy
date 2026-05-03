#pragma once
#include <condition_variable>
#include <cstddef>
#include <deque>
#include <functional>
#include <mutex>

namespace JobSystem {

class ContinuationQueue {
public:
  void Push(std::function<void()> fn) {
    if(!fn) {
      return;
    }
    {
      std::lock_guard<std::mutex> lock(_mtx);
      _queue.emplace_back(std::move(fn));
    }
    _cv.notify_one();
  }

  // Blocks until there is a task or Stop() is called.
  // Returns false when stopped and no more tasks.
  bool Pop(std::function<void()>& out) {
    std::unique_lock<std::mutex> lock(_mtx);
    _cv.wait(lock, [&] { return _stopped || !_queue.empty(); });

    if(_queue.empty()) {
      return false;
    }

    out = std::move(_queue.front());
    _queue.pop_front();
    return true;
  }

  void Stop() {
    {
      std::lock_guard<std::mutex> lock(_mtx);
      _stopped = true;
    }
    _cv.notify_all();
  }

private:
  std::mutex _mtx;
  std::condition_variable _cv;
  std::deque<std::function<void()>> _queue;
  bool _stopped{false};
};

} // namespace JobSystem
