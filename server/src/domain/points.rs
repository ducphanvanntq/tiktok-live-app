//! Session-owned TOP points, using the same wire contract as the Node bridge.
use super::event::GameEvent;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

const MAX_POINTS: i64 = 9_007_199_254_740_991;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PointScore {
    pub user_id: String,
    pub nickname: String,
    pub avatar: String,
    pub points: i64,
    pub reached_order: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PointsSnapshot {
    pub points_version: i64,
    pub points_revision: i64,
    pub point_scores: Vec<PointScore>,
}

#[derive(Debug, Default)]
pub struct PointsLeaderboard {
    players: BTreeMap<String, PointScore>,
    revision: i64,
    sequence: i64,
}

impl PointsLeaderboard {
    pub fn apply(&mut self, event: &GameEvent) -> bool {
        if event.user_id.is_empty() || event.user_id.starts_with("npc-") {
            return false;
        }
        let delta = match event.kind.as_str() {
            "like" => event.like_count.max(0),
            "gift" => event.diamond_count.max(0).saturating_mul(100),
            _ => 0,
        }
        .min(MAX_POINTS);
        let previous = self.players.get(&event.user_id);
        if previous.is_none() && delta == 0 {
            return false;
        }
        let old_points = previous.map_or(0, |p| p.points);
        let points = old_points.saturating_add(delta).min(MAX_POINTS);
        let nickname = if !event.nickname.trim().is_empty() {
            event.nickname.trim().to_string()
        } else {
            previous.map_or_else(|| event.user_id.clone(), |p| p.nickname.clone())
        };
        let avatar = if !event.avatar.is_empty() {
            event.avatar.clone()
        } else {
            previous.map_or_else(String::new, |p| p.avatar.clone())
        };
        if previous
            .is_some_and(|p| p.points == points && p.nickname == nickname && p.avatar == avatar)
        {
            return false;
        }
        let reached_order = if previous.is_none() || points > old_points {
            self.sequence += 1;
            self.sequence
        } else {
            previous.map_or(0, |p| p.reached_order)
        };
        self.players.insert(
            event.user_id.clone(),
            PointScore {
                user_id: event.user_id.clone(),
                nickname,
                avatar,
                points,
                reached_order,
            },
        );
        self.revision += 1;
        true
    }

    pub fn snapshot(&self) -> PointsSnapshot {
        let mut scores: Vec<_> = self.players.values().cloned().collect();
        scores.sort_by(|a, b| {
            b.points
                .cmp(&a.points)
                .then(a.reached_order.cmp(&b.reached_order))
                .then(a.user_id.cmp(&b.user_id))
        });
        scores.truncate(3);
        PointsSnapshot {
            points_version: 1,
            points_revision: self.revision,
            point_scores: scores,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn spectators_score_but_npcs_do_not_and_ties_keep_arrival_order() {
        let mut board = PointsLeaderboard::default();
        for id in ["b", "a", "c", "d", "npc-1"] {
            board.apply(&GameEvent {
                kind: "like".into(),
                user_id: id.into(),
                like_count: 10,
                spectator_only: true,
                ..Default::default()
            });
        }
        assert_eq!(
            board
                .snapshot()
                .point_scores
                .iter()
                .map(|p| p.user_id.as_str())
                .collect::<Vec<_>>(),
            ["b", "a", "c"]
        );
        board.apply(&GameEvent {
            kind: "gift".into(),
            user_id: "d".into(),
            diamond_count: 1,
            ..Default::default()
        });
        assert_eq!(board.snapshot().point_scores[0].points, 110);
        assert_eq!(board.snapshot().points_revision, 5);
    }
    #[test]
    fn totals_saturate_and_identity_updates_do_not_change_tie_order() {
        let mut board = PointsLeaderboard::default();
        let mut event = GameEvent {
            kind: "gift".into(),
            user_id: "a".into(),
            diamond_count: i64::MAX,
            ..Default::default()
        };
        board.apply(&event);
        board.apply(&event);
        assert_eq!(board.snapshot().points_revision, 1);
        event.kind = "chat".into();
        event.nickname = "New name".into();
        board.apply(&event);
        let score = &board.snapshot().point_scores[0];
        assert_eq!(score.points, MAX_POINTS);
        assert_eq!(score.reached_order, 1);
        assert_eq!(score.nickname, "New name");
    }
}
