//! Access to the synchronous [`tv_store::Store`] from async code.
//!
//! The store lives on its own OS thread and runs jobs one at a time, in the
//! order they were submitted. Async code never blocks on SQLite: it either
//! awaits a reply ([`StoreHandle::call`]) or fires and forgets
//! ([`StoreHandle::submit`]). Because jobs are ordered, a ban submitted by the
//! world task is visible to every login check submitted after it.

use std::sync::mpsc;
use std::thread;

use tokio::sync::oneshot;
use tv_store::Store;

type Job = Box<dyn FnOnce(&mut Store) + Send>;

/// The store thread has stopped (it panicked, or the server is shutting down).
#[derive(Debug, thiserror::Error)]
#[error("the store is not available")]
pub struct StoreGone;

/// A cheap, cloneable handle to the store thread.
#[derive(Clone)]
pub struct StoreHandle {
    jobs: mpsc::Sender<Job>,
}

impl StoreHandle {
    /// Moves `store` onto a new thread. The thread ends when every handle
    /// has been dropped and the queued jobs are done.
    pub fn spawn(store: Store) -> (StoreHandle, thread::JoinHandle<()>) {
        let (jobs, rx) = mpsc::channel::<Job>();
        let thread = thread::Builder::new()
            .name("tv-store".into())
            .spawn(move || {
                let mut store = store;
                while let Ok(job) = rx.recv() {
                    job(&mut store);
                }
            })
            .expect("can spawn the store thread");
        (StoreHandle { jobs }, thread)
    }

    /// Runs `f` on the store thread and waits for its result.
    pub async fn call<R, F>(&self, f: F) -> Result<R, StoreGone>
    where
        R: Send + 'static,
        F: FnOnce(&mut Store) -> R + Send + 'static,
    {
        let (tx, rx) = oneshot::channel();
        self.jobs
            .send(Box::new(move |store| {
                let _ = tx.send(f(store));
            }))
            .map_err(|_| StoreGone)?;
        rx.await.map_err(|_| StoreGone)
    }

    /// Queues `f` on the store thread without waiting.
    pub fn submit<F>(&self, f: F)
    where
        F: FnOnce(&mut Store) + Send + 'static,
    {
        if self.jobs.send(Box::new(f)).is_err() {
            tracing::error!("store thread is gone; a write was lost");
        }
    }
}
