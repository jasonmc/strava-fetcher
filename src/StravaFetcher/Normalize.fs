namespace StravaFetcher

open System
open System.Globalization

module Normalize =
    let private metersToKilometers meters = meters / 1000.0

    let private secondsToHours seconds = float seconds / 3600.0

    let private isCycling (activity: Activity) =
        let knownCycling =
            set
                [ "Ride"
                  "VirtualRide"
                  "EBikeRide"
                  "MountainBikeRide"
                  "GravelRide"
                  "Handcycle"
                  "Velomobile"
                  "EMountainBikeRide" ]

        knownCycling.Contains(activity.sport_type)
        || activity.sport_type.EndsWith("Ride", StringComparison.Ordinal)
        || (activity.``type`` |> Option.defaultValue "" = "Ride")

    let private weekStart (value: DateTimeOffset) =
        let day = int value.DayOfWeek
        let delta = (day + 6) % 7
        value.Date.AddDays(float -delta)

    let private round2 (value: float) =
        Math.Round(value, 2, MidpointRounding.AwayFromZero)

    let private normalizeRideTotals (rideTotals: RideTotals) =
        { rideTotals with
            distance = rideTotals.distance |> metersToKilometers |> round2
            elevation_gain = rideTotals.elevation_gain |> round2
            moving_time = rideTotals.moving_time |> round2
            elapsed_time = rideTotals.elapsed_time |> round2 }

    let build athlete stats (activities: Activity list) =
        let cyclingActivities =
            activities |> List.filter isCycling |> List.sortBy (fun a -> a.start_date)

        let weeklyKilometers =
            cyclingActivities
            |> List.groupBy (fun a -> weekStart a.start_date)
            |> List.sortBy fst
            |> List.map (fun (week, items) ->
                {| week_start = week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                   kilometers = items |> List.sumBy (fun a -> metersToKilometers a.distance) |> round2 |})

        let weeklyHours =
            cyclingActivities
            |> List.groupBy (fun a -> weekStart a.start_date)
            |> List.sortBy fst
            |> List.map (fun (week, items) ->
                {| week_start = week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                   hours = items |> List.sumBy (fun a -> secondsToHours a.moving_time) |> round2 |})

        let annualRideTotals =
            cyclingActivities
            |> List.groupBy (fun a -> a.start_date.Year)
            |> List.sortBy fst
            |> List.map (fun (year, items) ->
                {| year = year
                   rides = items.Length
                   kilometers = items |> List.sumBy (fun a -> metersToKilometers a.distance) |> round2
                   hours = items |> List.sumBy (fun a -> secondsToHours a.moving_time) |> round2
                   elevation = items |> List.sumBy (fun a -> a.total_elevation_gain) |> round2 |})

        {| athlete_id = athlete.id
           fetched_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
           biggest_ride_distance = round2 (metersToKilometers stats.biggest_ride_distance)
           biggest_climb_elevation_gain = round2 stats.biggest_climb_elevation_gain
           recent_ride_totals = normalizeRideTotals stats.recent_ride_totals
           ytd_ride_totals = normalizeRideTotals stats.ytd_ride_totals
           all_ride_totals = normalizeRideTotals stats.all_ride_totals
           weekly_ride_kilometers = weeklyKilometers
           weekly_ride_hours = weeklyHours
           annual_ride_totals = annualRideTotals |}
