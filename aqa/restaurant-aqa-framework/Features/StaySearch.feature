@ui
Feature: Stay search

  What a visitor can do on the public accommodation site: search by location, filter,
  sort and open a listing. The site is read-only for the tests and its data changes
  daily, so scenarios check properties of what is shown, never specific listings or prices.

  Background:
    Given the visitor is on the stay search

  Rule: A visitor can search by location

    @smoke @data-dependent
    Scenario: Searching a country lists places in it
      When the visitor searches for stays in a country
      Then the search shows that country
      And the results stay in English
      And some places are offered
      And every place shown has a name, a price and a link
      And every place shown is in that country
      And only the necessary cookies are accepted

    @data-dependent
    Scenario: A returning visitor is not asked about cookies again
      When the visitor searches for stays in a country
      And the visitor opens the stay search again
      And the visitor searches for stays in a country
      Then the cookie banner is not shown
      And some places are offered
      And only the necessary cookies are accepted
