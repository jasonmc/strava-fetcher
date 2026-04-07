namespace StravaFetcher

open System

type TokenResponse =
    { access_token: string
      refresh_token: string }

type Athlete = { id: int64 }

type RideTotals =
    { count: int
      distance: float
      moving_time: int
      elapsed_time: int
      elevation_gain: float
      achievement_count: int }

type AthleteStats =
    { biggest_ride_distance: float
      biggest_climb_elevation_gain: float
      recent_ride_totals: RideTotals
      ytd_ride_totals: RideTotals
      all_ride_totals: RideTotals }

type Activity =
    { start_date: DateTimeOffset
      distance: float
      moving_time: int
      total_elevation_gain: float
      sport_type: string
      ``type``: string option }
